﻿
using System.Windows.Forms;

namespace GraphView.Transaction
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using GraphView.RecordRuntime;
    using Newtonsoft.Json.Linq;

    internal class VersionNode
    {
        public VersionEntry VersionEntry;
        // TODO: confirm whether it's a reference variable
        public VersionNode Next;
    }

    internal class VersionList
    {
        private VersionNode head;

        public VersionList()
        {
            this.head = new VersionNode();
            head.VersionEntry = new VersionEntry(false, long.MaxValue, false, long.MaxValue, null);
            head.Next = null;
        }

        public void PushFront(VersionEntry versionEntry)
        {
            VersionNode newNode = new VersionNode();
            newNode.VersionEntry = versionEntry;

            do
            {
                newNode.Next = this.head;
            }
            while (newNode.Next != Interlocked.CompareExchange(ref this.head, newNode, newNode.Next));
        }

        public bool ChangeNodeValue(VersionEntry oldVersion, VersionEntry newVersion)
        {
            VersionNode node = this.head;
            while (node != null && node.VersionEntry.Record != null)
            {
                // try to find the old version
                if (node.VersionEntry == oldVersion)
                {
                    return oldVersion == Interlocked.CompareExchange(ref node.VersionEntry, newVersion, oldVersion);
                }
                node = node.Next;
            }

            return false;
        }

        public IList<VersionEntry> ToList()
        {
            IList<VersionEntry> versionList = new List<VersionEntry>();
            VersionNode node = this.head;
            while (node != null && node.VersionEntry.Record != null)
            {
                versionList.Add(node.VersionEntry);
                node = node.Next;
            }

            return versionList;
        }
    }

    internal abstract class SingletonVersionTable : VersionTable, IVersionedTableStore
    {
        public JObject GetJson(object key, Transaction tx)
        {
            throw new NotImplementedException();
        }

        public IList<JObject> GetRangeJsons(object lowerKey, object upperKey, Transaction tx)
        {
            throw new NotImplementedException();
        }

        public IList<object> GetRangeRecordKeyList(object lowerValue, object upperValue, Transaction tx)
        {
            throw new NotImplementedException();
        }

        public IList<object> GetRecordKeyList(object value, Transaction tx)
        {
            throw new NotImplementedException();
        }

        public bool InsertJson(object key, JObject record, Transaction tx)
        {
            throw new NotImplementedException();
        }

        public bool UpdateJson(object key, JObject record, Transaction tx)
        {
            throw new NotImplementedException();
        }

        public bool DeleteJson(object key, Transaction tx)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// A version table implementation in single machine environment.
    /// Use a Dictionary.
    /// </summary>
    internal partial class SingletonVersionDictionary : SingletonVersionTable
    {
        private readonly Dictionary<object, VersionList> dict;
        private readonly object listLock;

        public SingletonVersionDictionary(string tableId)
        {
            this.dict = new Dictionary<object, VersionList>();
            this.listLock = new object();
            this.TableId = tableId;
        }

        internal override IList<VersionEntry> GetVersionList(object recordKey)
        {
            if (!this.dict.ContainsKey(recordKey))
            {
                return null;
            }

            return this.dict[recordKey].ToList();
        }

        internal override void InsertAndUploadVersion(object recordKey, VersionEntry version)
        {
            //the version list does not exist, create a new list
            if (!this.dict.ContainsKey(recordKey))
            {
                lock (this.listLock)
                {
                    if (!this.dict.ContainsKey(recordKey))
                    {
                        this.dict[recordKey] = new VersionList();
                    }
                }
            }

            this.dict[recordKey].PushFront(version);
        }

        internal override bool UpdateAndUploadVersion(object recordKey, VersionEntry oldVersion, VersionEntry newVersion)
        {
            return this.dict[recordKey].ChangeNodeValue(oldVersion, newVersion);
        }

    }

    internal partial class SingletonVersionDictionary : IVersionedTableStore
    {
        /// <summary>
        /// Read a record from the given recordKey
        /// return null if not found
        /// </summary>
        public new JObject GetJson(object key, Transaction tx)
        {
            VersionEntry versionEntry = this.GetVersionEntry(key, tx.BeginTimestamp);
            if (versionEntry != null)
            {
                tx.AddReadSet(this.TableId, key, versionEntry.BeginTimestamp);
                tx.AddScanSet(this.TableId, key, tx.BeginTimestamp, true);
                return versionEntry.Record;
            }

            return null;
        }

        /// <summary>
        /// Read a list of records where their keys are between lowerKey and upperKey
        /// </summary>
        public new IList<JObject> GetRangeJsons(object lowerKey, object upperKey, Transaction tx)
        {
            List<JObject> jObjectValues = new List<JObject>();
            IComparable lowerComparableKey = lowerKey as IComparable;
            IComparable upperComparebleKey = upperKey as IComparable;

            if (lowerComparableKey == null || upperComparebleKey == null)
            {
                throw new ArgumentException("lowerKey and upperKey must be comparable");
            }

            foreach (var key in this.dict.Keys)
            {
                IComparable comparableKey = key as IComparable;
                if (comparableKey == null)
                {
                    throw new RecordServiceException("recordKey must be comparable");
                }

                if (lowerComparableKey.CompareTo(comparableKey) <= 0 
                    && upperComparebleKey.CompareTo(comparableKey) >= 0)
                {
                    VersionEntry versionEntry = this.GetVersionEntry(key, tx.BeginTimestamp);
                    if (versionEntry == null)
                    {
                        // false means no visiable version for this key
                        tx.AddScanSet(this.TableId, key, tx.BeginTimestamp, false);
                    }
                    else
                    {
                        jObjectValues.Add(versionEntry.Record);
                        // true means we found a visiable version for this key
                        tx.AddScanSet(this.TableId, key, tx.BeginTimestamp, true);
                        tx.AddReadSet(this.TableId, key, versionEntry.BeginTimestamp);
                    }
                }
            }

            return jObjectValues;
        }

        /// <summary>
        /// Get the union set of keys list for every value between lowerValue and uppperValue in index-table
        /// index format: value => [key1, key2, ...]
        /// </summary>
        public new IList<object> GetRangeRecordKeyList(object lowerValue, object upperValue, Transaction tx)
        {
            HashSet<object> keyHashset = new HashSet<object>();
            IComparable lowerComparableValue = lowerValue as IComparable;
            IComparable upperComparableValue = upperValue as IComparable;

            if (lowerComparableValue == null || upperComparableValue == null)
            {
                throw new ArgumentException("lowerValue and upperValue must be comparable");
            }

            foreach (var key in this.dict.Keys)
            {
                IComparable comparableKey = key as IComparable;
                if (comparableKey == null)
                {
                    throw new ArgumentException("recordKey must be comparable");
                }

                if (lowerComparableValue.CompareTo(comparableKey) <= 0
                    && upperComparableValue.CompareTo(comparableKey) >= 0)
                {
                    VersionEntry versionEntry = this.GetVersionEntry(key, tx.BeginTimestamp);
                    if (versionEntry == null)
                    {
                        // false means no visiable version for this key
                        tx.AddScanSet(this.TableId, key, tx.BeginTimestamp, false);
                    }
                    else
                    {
                        JObject record = versionEntry.Record;
                        IndexValue indexValue = record.ToObject<IndexValue>();
                        if (indexValue == null)
                        {
                            throw new RecordServiceException(@"wrong format of index, should be 
                                {""keys"":[""key1"", ""key2"", ...]}");
                        }

                        keyHashset.UnionWith(indexValue.Keys);
                        // true means we found a visiable version for this key
                        tx.AddScanSet(this.TableId, key, tx.BeginTimestamp, true);
                        tx.AddReadSet(this.TableId, key, versionEntry.BeginTimestamp);
                    }
                }
            }

            return keyHashset.ToList<object>();
        }

        /// <summary>
        ///  This method will return all keys for a value in a index-table
        /// index format: value => [key1, key2, ...]
        /// </summary>
        public new IList<object> GetRecordKeyList(object value, Transaction tx)
        {
            VersionEntry versionEntry = this.GetVersionEntry(value, tx.BeginTimestamp);
            if (versionEntry != null)
            {
                JObject record = versionEntry.Record;
                IndexValue indexValue = record.ToObject<IndexValue>();
                if (indexValue == null)
                {
                    throw new RecordServiceException(@"wrong format of index, should be 
                                {""keys"":[""key1"", ""key2"", ...]}");
                }

                tx.AddReadSet(this.TableId, value, versionEntry.BeginTimestamp);
                tx.AddScanSet(this.TableId, value, tx.BeginTimestamp, true);

                return indexValue.Keys;
            }

            return null;
        }

        public new bool InsertJson(object key, JObject record, Transaction tx)
        {
            VersionEntry versionEntry = this.GetVersionEntry(key, tx.BeginTimestamp);
            
            // insert the version if we have not found a visiable version
            if (versionEntry != null)
            {
                return false;
            }

            bool hasInserted = this.InsertVersion(key, record, tx.TxId, tx.BeginTimestamp);
            if (hasInserted)
            {
                tx.AddScanSet(this.TableId, key, tx.BeginTimestamp, false);
                tx.AddWriteSet(this.TableId, key, tx.TxId, false);
            }
            return hasInserted;
        }

        public new bool UpdateJson(object key, JObject record, Transaction tx)
        {
            VersionEntry versionEntry = this.GetVersionEntry(key, tx.BeginTimestamp);
            if (versionEntry == null)
            {
                return false;
            }

            VersionEntry oldVersionEntry = null, newVersionEntry = null;
            bool hasUpdated = this.UpdateVersion(key, record, tx.TxId, tx.BeginTimestamp, 
                out oldVersionEntry, out newVersionEntry);
            if (hasUpdated)
            {
                // pass the old version' begin timestamp to find old version
                tx.AddWriteSet(this.TableId, key, oldVersionEntry.BeginTimestamp, true);
                // pass the new version's begin timestamp to find the new version
                tx.AddWriteSet(this.TableId, key, tx.TxId, false);
            }
            return hasUpdated;
        }

        public new bool DeleteJson(object key, Transaction tx)
        {
            VersionEntry versionEntry = this.GetVersionEntry(key, tx.BeginTimestamp);
            if (versionEntry == null)
            {
                return false;
            }

            VersionEntry deletedVersionEntry = null;
            bool hasDeleted = this.DeleteVersion(key, tx.TxId, tx.BeginTimestamp, out deletedVersionEntry);
            if (hasDeleted)
            {
                // pass the old version's begin timestamp to find the old version
                tx.AddWriteSet(this.TableId, key, versionEntry.BeginTimestamp, true);
            }
            return hasDeleted;
        }
        
        /// <summary>
        /// Get a visiable version entry from versionTable with the
        /// </summary>
        internal VersionEntry GetVersionEntry(object key, long readTimestamp)
        {
            IList<VersionEntry> versionEntryList = this.GetVersionList(key);
            if (versionEntryList == null)
            {
                return null;
            }

            foreach (VersionEntry versionEntry in versionEntryList)
            {
                if (this.CheckVersionVisibility(versionEntry, readTimestamp))
                {
                    return versionEntry;
                }
            }

            return null;
        }
    }
}