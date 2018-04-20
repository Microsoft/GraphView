﻿
namespace GraphView.Transaction
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Diagnostics;

    internal delegate void Procedure();

    internal enum TxProgress
    {
        Read,
        Insert,
        Update,
        Delete,
        Open,
        Final,
        Close,
    }

    public class TransactionExecution
    {
        internal static readonly long DEFAULT_TX_BEGIN_TIMESTAMP = -1L;

        internal static readonly int POSTPROCESSING_LIST_MAX_CAPACITY = 2;

        internal static readonly long UNSET_TX_COMMIT_TIMESTAMP = -2L;

        private readonly ILogStore logStore;

        private readonly VersionDb versionDb;

        internal readonly long txId;

        private Stack<TxRequest> requestStack;

        internal long commitTs;

        internal long maxCommitTsOfWrites;

        internal long beginTimestamp;

        internal Dictionary<string, Dictionary<object, ReadSetEntry>> readSet;

        internal Dictionary<string, Dictionary<object, object>> writeSet;

        internal Dictionary<string, Dictionary<object, List<PostProcessingEntry>>> abortSet;

        internal Dictionary<string, Dictionary<object, List<PostProcessingEntry>>> commitSet;

        internal Dictionary<string, Dictionary<object, long>> largestVersionKeyMap;

        private Queue<Tuple<string, object>> writeKeyList;

        private Stack<Tuple<string, VersionEntry>> validateKeyList;

        private Queue<VersionEntry> readVersionList;

        internal Procedure CurrentProc { get; private set; }

        internal TxProgress Progress { get; private set; }

        public TransactionExecution(ILogStore logStore, VersionDb versionDb)
        {
            this.logStore = logStore;
            this.versionDb = versionDb;
            this.readSet = new Dictionary<string, Dictionary<object, ReadSetEntry>>();
            this.writeSet = new Dictionary<string, Dictionary<object, object>>();
            this.abortSet = new Dictionary<string, Dictionary<object, List<PostProcessingEntry>>>();
            this.commitSet = new Dictionary<string, Dictionary<object, List<PostProcessingEntry>>>();
            this.largestVersionKeyMap = new Dictionary<string, Dictionary<object, long>>();

            this.txId = this.versionDb.InsertNewTx();
            this.commitTs = TxTableEntry.DEFAULT_COMMIT_TIME;
            this.maxCommitTsOfWrites = -1L;
            this.beginTimestamp = TransactionExecution.DEFAULT_TX_BEGIN_TIMESTAMP;
            this.Progress = TxProgress.Open;

            this.requestStack = new Stack<TxRequest>();
        }

        private void PopulateWriteKeyList()
        {
            this.writeKeyList = new Queue<Tuple<string, object>>();
            foreach (string tableId in this.writeSet.Keys)
            {
                foreach (object recordKey in this.writeSet[tableId].Keys)
                {
                    this.writeKeyList.Enqueue(new Tuple<string, object>(tableId, recordKey));
                }
            }
        }

        internal void Upload()
        {
            this.Progress = TxProgress.Final;

            if (this.writeKeyList == null)
            {
                this.PopulateWriteKeyList();
            }

            while (this.writeKeyList.Count > 0 || this.requestStack.Count > 0)
            {
                if (requestStack.Count == 0)     // Prior write set entry has been uploaded successfully
                {
                    Debug.Assert(writeKeyList.Count > 0);

                    Tuple<string, object> writeTuple = writeKeyList.Dequeue();

                    string tableId = writeTuple.Item1;
                    object recordKey = writeTuple.Item2;
                    object payload = this.writeSet[tableId][recordKey];

                    VersionEntry newImageEntry = new VersionEntry(
                                recordKey,
                                this.largestVersionKeyMap[tableId][recordKey] + 1,
                                payload,
                                this.txId);

                    UploadVersionRequest txRequest = this.versionDb.EnqueueUploadNewVersionEntry(
                        tableId, recordKey, newImageEntry.VersionKey, newImageEntry);

                    // Enqueues the upload request and returns the control to the caller
                    requestStack.Push(txRequest);
                    return;
                }
                else if (requestStack.Count == 1 && requestStack.Peek() is UploadVersionRequest)
                {
                    if (!requestStack.Peek().Finished)
                    {
                        // The prior request hasn't been processed. Returns the control to the caller.
                        return;
                    }
                    UploadVersionRequest uploadReq = (requestStack.Pop()) as UploadVersionRequest;

                    bool uploadSuccess = uploadReq.Result == null ? false : (bool)uploadReq.Result;
                    if (!uploadSuccess)
                    {
                        // Failed to upload the new image. Moves to the abort phase. 
                        this.CurrentProc = new Procedure(this.Abort);
                        this.CurrentProc();
                        return;
                    }

                    string tableId = uploadReq.TableId;
                    object recordKey = uploadReq.RecordKey;

                    // Add the info to the abortSet
                    this.AddVersionToAbortSet(tableId, recordKey, uploadReq.VersionKey,
                        VersionEntry.DEFAULT_BEGIN_TIMESTAMP, VersionEntry.DEFAULT_END_TIMESTAMP);
                    // Add the info to the commitSet
                    this.AddVersionToCommitSet(tableId, recordKey, uploadReq.VersionKey,
                        TransactionExecution.UNSET_TX_COMMIT_TIMESTAMP, long.MaxValue);

                    if (!this.readSet.ContainsKey(tableId) || !this.readSet[tableId].ContainsKey(recordKey))
                    {
                        // The write-set record is an insert record. 
                        // Moves to the next write-set record or the next phase.
                        continue;
                    }

                    ReadSetEntry readVersion = this.readSet[tableId][recordKey];
                    // Initiates a new request to append the new image to the tail of the version list.
                    // The tail entry could be [Ts, inf, -1], [Ts, inf, txId1] or [-1, -1, txId1].
                    // The first case indicates that no concurrent tx is locking the tail.
                    // The second case indicates that one concurrent tx is holding the tail. 
                    // The third case means that a concurrent tx is creating a new tail, which was seen by this tx. 
                    ReplaceVersionRequest replaceReq = this.versionDb.EnqueueReplaceVersionEntry(
                        tableId,
                        recordKey,
                        readVersion.VersionKey,
                        readVersion.BeginTimestamp,
                        long.MaxValue,
                        this.txId,
                        VersionEntry.EMPTY_TXID,
                        long.MaxValue);

                    this.requestStack.Push(replaceReq);
                    return;
                }
                else if (requestStack.Count == 1 && requestStack.Peek() is ReplaceVersionRequest)
                {
                    if (!requestStack.Peek().Finished)
                    {
                        // The prior request hasn't been processed. Returns the control to the caller.
                        return;
                    }

                    ReplaceVersionRequest replaceReq = (requestStack.Pop()) as ReplaceVersionRequest;
                    VersionEntry versionEntry = replaceReq.Result as VersionEntry;
                    if (versionEntry == null)
                    {
                        this.CurrentProc = new Procedure(this.Abort);
                        this.CurrentProc();
                        return;
                    }

                    long rolledBackBegin = versionEntry.BeginTimestamp;
                    this.maxCommitTsOfWrites = Math.Max(this.maxCommitTsOfWrites, versionEntry.MaxCommitTs);

                    if (versionEntry.TxId == this.txId) // Appending to the tail was successful
                    {
                        // Add the updated tail to the abort set
                        this.AddVersionToAbortSet(
                            replaceReq.TableId,
                            replaceReq.RecordKey,
                            replaceReq.VersionKey,
                            rolledBackBegin,
                            long.MaxValue);
                        // Add the updated tail to the commit set
                        this.AddVersionToCommitSet(
                            replaceReq.TableId,
                            replaceReq.RecordKey,
                            replaceReq.VersionKey,
                            rolledBackBegin,
                            TransactionExecution.UNSET_TX_COMMIT_TIMESTAMP);

                        // Move on to the next write-set record or the next phase
                        continue;
                    }
                    else if (versionEntry.TxId >= 0)
                    {
                        // The first try was unsuccessful because the tail is hold by another concurrent tx. 
                        // If the concurrent tx has finished (committed or aborted), there is a chance for this tx
                        // to re-gain the lock. 
                        // Enqueues a request to check the status of the tx that is holding the tail.
                        TxRequest txStatusReq = this.versionDb.EnqueueGetTxEntry(versionEntry.TxId);
                        this.requestStack.Push(replaceReq);
                        this.requestStack.Push(txStatusReq);
                        return;
                    }
                    else
                    {
                        // The new version is failed to append to the tail of the version list, 
                        // because the old tail seen by this tx is not the tail anymore

                        this.CurrentProc = new Procedure(this.Abort);
                        this.CurrentProc();
                        return;
                    }
                }
                else if (requestStack.Count == 2 && requestStack.Peek() is GetTxEntryRequest)
                {
                    if (!requestStack.Peek().Finished)
                    {
                        // The prior request hasn't been processed. Returns the control to the caller.
                        return;
                    }

                    GetTxEntryRequest getTxReq = requestStack.Pop() as GetTxEntryRequest;
                    ReplaceVersionRequest replaceReq = requestStack.Pop() as ReplaceVersionRequest;

                    TxTableEntry conflictTxStatus = getTxReq.Result as TxTableEntry;
                    if (conflictTxStatus == null || conflictTxStatus.Status == TxStatus.Ongoing)
                    {
                        this.CurrentProc = new Procedure(this.Abort);
                        this.CurrentProc();
                        return;
                    }

                    VersionEntry versionEntry = replaceReq.Result as VersionEntry;
                    // The new tail was created by a concurrent tx, yet has not been post-processed. 
                    // The current tx tries to update the tail to the post-processing image and obtain the lock.
                    if (versionEntry.EndTimestamp == VersionEntry.DEFAULT_END_TIMESTAMP)
                    {
                        // Only if a new tail's owner tx has been committed, can it be seen by 
                        // the current tx, who is trying to gain the lock of the tail. 
                        Debug.Assert(conflictTxStatus.Status == TxStatus.Committed);

                        ReplaceVersionRequest retryRequest = this.versionDb.EnqueueReplaceVersionEntry(
                            replaceReq.TableId,
                            replaceReq.RecordKey,
                            versionEntry.VersionKey,
                            conflictTxStatus.CommitTime,
                            long.MaxValue,
                            this.txId,
                            conflictTxStatus.TxId,
                            VersionEntry.DEFAULT_END_TIMESTAMP);

                        this.requestStack.Push(replaceReq);
                        this.requestStack.Push(retryRequest);
                        return;
                    }
                    // The old tail was locked by a concurrent tx, which has finished but has not released the lock. 
                    // The current lock tries to replace the lock's owner to itself. 
                    else if (versionEntry.EndTimestamp == long.MaxValue)
                    {
                        // The owner tx of the lock has committed. This version entry is not the tail anymore.
                        if (conflictTxStatus.Status == TxStatus.Committed)
                        {
                            this.CurrentProc = new Procedure(this.Abort);
                            this.CurrentProc();
                            return;
                        }
                        else if (conflictTxStatus.Status == TxStatus.Aborted)
                        {
                            ReplaceVersionRequest retryRequest = this.versionDb.EnqueueReplaceVersionEntry(
                                replaceReq.TableId,
                                replaceReq.RecordKey,
                                versionEntry.VersionKey,
                                versionEntry.BeginTimestamp,
                                long.MaxValue,
                                this.txId,
                                conflictTxStatus.TxId,
                                long.MaxValue);

                            this.requestStack.Push(replaceReq);
                            this.requestStack.Push(retryRequest);
                            return;
                        }
                    }
                }
                else if (requestStack.Count == 2 && requestStack.Peek() is ReplaceVersionRequest)
                {
                    if (!requestStack.Peek().Finished)
                    {
                        // The prior request hasn't been processed. Returns the control to the caller.
                        return;
                    }

                    ReplaceVersionRequest retryReq = requestStack.Pop() as ReplaceVersionRequest;
                    requestStack.Pop();

                    VersionEntry retryEntry = retryReq.Result as VersionEntry;
                    if (retryEntry == null || retryEntry.TxId != this.txId)
                    {
                        this.CurrentProc = new Procedure(this.Abort);
                        this.CurrentProc();
                        return;
                    }

                    long rolledBackBegin = retryEntry.BeginTimestamp;
                    this.maxCommitTsOfWrites = Math.Max(this.maxCommitTsOfWrites, retryEntry.MaxCommitTs);

                    // Add the updated tail to the abort set
                    this.AddVersionToAbortSet(retryReq.TableId, retryReq.RecordKey, retryReq.VersionKey,
                        rolledBackBegin, long.MaxValue);

                    // Add the updated tail to the commit set
                    this.AddVersionToCommitSet(retryReq.TableId, retryReq.RecordKey, retryReq.VersionKey,
                        rolledBackBegin, TransactionExecution.UNSET_TX_COMMIT_TIMESTAMP);

                    continue;
                }
                else
                {
                    throw new TransactionException("An illegal state of the uploading phase.");
                }
            }

            // Move on to the next phase
            this.writeKeyList = null;
            this.CurrentProc = new Procedure(this.SetCommitTimestamp);
            this.CurrentProc();
            return;
        }

        private void AddVersionToAbortSet(string tableId, object recordKey, long versionKey, long beginTs, long endTs)
        {
            if (!this.abortSet.ContainsKey(tableId))
            {
                this.abortSet[tableId] = new Dictionary<object, List<PostProcessingEntry>>();
            }

            if (!this.abortSet[tableId].ContainsKey(recordKey))
            {
                this.abortSet[tableId][recordKey] = new List<PostProcessingEntry>(TransactionExecution.POSTPROCESSING_LIST_MAX_CAPACITY);
            }
            this.abortSet[tableId][recordKey].Add(new PostProcessingEntry(versionKey, beginTs, endTs));
        }

        private void AddVersionToCommitSet(string tableId, object recordKey, long versionKey, long beginTs, long endTs)
        {
            if (!this.commitSet.ContainsKey(tableId))
            {
                this.commitSet[tableId] = new Dictionary<object, List<PostProcessingEntry>>();
            }

            if (!this.commitSet[tableId].ContainsKey(recordKey))
            {
                this.commitSet[tableId][recordKey] = new List<PostProcessingEntry>(TransactionExecution.POSTPROCESSING_LIST_MAX_CAPACITY);
            }
            this.commitSet[tableId][recordKey].Add(new PostProcessingEntry(versionKey, beginTs, endTs));
        }

        internal void SetCommitTimestamp()
        {
            if (this.commitTs < 0)
            {
                long proposedCommitTs = this.maxCommitTsOfWrites + 1;

                foreach (string tableId in this.readSet.Keys)
                {
                    foreach (object recordKey in this.readSet[tableId].Keys)
                    {
                        ReadSetEntry entry = this.readSet[tableId][recordKey];
                        if (this.writeSet.ContainsKey(tableId) && this.writeSet[tableId].ContainsKey(recordKey))
                        {
                            proposedCommitTs = Math.Max(proposedCommitTs, entry.BeginTimestamp + 1);
                        }
                        else
                        {
                            proposedCommitTs = Math.Max(proposedCommitTs, entry.BeginTimestamp);
                        }
                    }
                }

                this.commitTs = proposedCommitTs;
            }

            if (this.requestStack.Count == 0)
            {
                SetCommitTsRequest setTsReq = this.versionDb.EnqueueSetCommitTs(this.txId, this.commitTs);
                this.requestStack.Push(setTsReq);
                return;
            }
            else if (this.requestStack.Count == 1)
            {
                TxRequest req = this.requestStack.Pop();
                if (!req.Finished)
                {
                    // The prior request hasn't been processed. Returns the control to the caller.
                    return;
                }

                SetCommitTsRequest setTsReq = req as SetCommitTsRequest;
                long commitTime = setTsReq.Result == null ? -1 : (long)setTsReq.Result;
                if (commitTime < 0)
                {
                    this.CurrentProc = new Procedure(this.Abort);
                    this.CurrentProc();
                    return;
                } 
                else
                {
                    this.commitTs = commitTime;
                    this.CurrentProc = new Procedure(this.Validate);
                    this.CurrentProc();
                    return;
                }
            }
            else
            {
                throw new TransactionException("An illegal state when setting the tx's commit timestamp.");
            }
        }

        internal void Validate()
        {
            if (this.validateKeyList == null)
            {
                if (this.requestStack.Count == 0)
                {
                    // Enqueues all requests re-reading the version to be validated 
                    foreach (string tableId in this.readSet.Keys)
                    {
                        foreach (object recordKey in this.readSet[tableId].Keys)
                        {
                            if (this.writeSet.ContainsKey(tableId) && this.writeSet[tableId].ContainsKey(recordKey))
                            {
                                continue;
                            }

                            ReadVersionRequest readReq = this.versionDb.EnqueueGetVersionEntryByKey(
                                tableId, recordKey, readSet[tableId][recordKey].VersionKey);
                            this.requestStack.Push(readReq);
                        }
                    }

                    // Enqueues all requests re-reading the versions. 
                    return;
                }

                this.validateKeyList = new Stack<Tuple<string, VersionEntry>>(this.requestStack.Count);
                foreach (TxRequest req in this.requestStack)
                {
                    if (!req.Finished)
                    {
                        // If any of the re-reading ops has not finished, returns the control to the caller.
                        this.validateKeyList = null;
                        return;
                    }

                    ReadVersionRequest readVersionReq = req as ReadVersionRequest;
                    VersionEntry readEntry = req.Result as VersionEntry;
                    if (readEntry == null)
                    {
                        this.CurrentProc = new Procedure(this.Abort);
                        this.CurrentProc();
                        return;
                    }

                    this.validateKeyList.Push(Tuple.Create(readVersionReq.TableId, readEntry));
                }

                this.requestStack.Clear();
            }

            while (this.validateKeyList.Count > 0 || this.requestStack.Count > 0)
            {
                if (this.requestStack.Count == 0)
                {
                    Tuple<string, VersionEntry> valTuple = this.validateKeyList.Peek();

                    string tableId = valTuple.Item1;
                    VersionEntry readVersion = valTuple.Item2;

                    if (readVersion.MaxCommitTs >= this.commitTs)
                    {
                        // No need to update the version's maxCommitTs.
                        // Check whether or not the re-read version is occupied by another tx.
                        if (readVersion.TxId != VersionEntry.EMPTY_TXID)
                        {
                            // A concurrent tx is locking the version. Checks the tx's status to decide how to move forward, 
                            // i.e., abort or pass validation.
                            GetTxEntryRequest getTxReq = this.versionDb.EnqueueGetTxEntry(readVersion.TxId);
                            this.requestStack.Push(getTxReq);
                            return;
                        }
                        else
                        {
                            if (this.commitTs > readVersion.EndTimestamp)
                            {
                                // A new version has been created before this tx can commit.
                                // Abort the tx.

                                this.CurrentProc = new Procedure(this.Abort);
                                this.CurrentProc();
                                return;
                            }
                            else
                            {
                                // No new version has bee created. This record passes validation. 
                                this.validateKeyList.Pop();
                                continue;
                            }
                        }
                    }
                    else
                    {
                        // Updates the version's max commit timestamp
                        UpdateVersionMaxCommitTsRequest updateMaxTsReq = this.versionDb.EnqueueUpdateVersionMaxCommitTs(
                            tableId, readVersion.RecordKey, readVersion.VersionKey, this.commitTs);
                        this.requestStack.Push(updateMaxTsReq);
                        return;
                    }
                }
                else if (this.requestStack.Count == 1 && this.requestStack.Peek() is UpdateVersionMaxCommitTsRequest)
                {
                    TxRequest req = this.requestStack.Pop();
                    if (!req.Finished)
                    {
                        // The pending request hasn't been processed. Returns the control to the caller.
                        return;
                    }

                    UpdateVersionMaxCommitTsRequest updateMaxTsReq = req as UpdateVersionMaxCommitTsRequest;
                    VersionEntry readEntry = updateMaxTsReq.Result as VersionEntry;
                    if (req == null || readEntry == null)
                    {
                        this.CurrentProc = new Procedure(this.Abort);
                        this.CurrentProc();
                        return;
                    }

                    // Successfully updated the version's max commit ts. 
                    // Replaces the old version entry by the new one.  
                    Tuple<string, VersionEntry> valTuple = this.validateKeyList.Pop();
                    this.validateKeyList.Push(Tuple.Create(valTuple.Item1, readEntry));
                    continue;
                }
                else if (this.requestStack.Count == 1 && this.requestStack.Peek() is GetTxEntryRequest)
                {
                    if (!this.requestStack.Peek().Finished)
                    {
                        // The pending request hasn't been processed. Returns the control to the caller.
                        return;
                    }

                    GetTxEntryRequest getTxReq = this.requestStack.Pop() as GetTxEntryRequest;
                    TxTableEntry txEntry = getTxReq.Result as TxTableEntry;
                    if (txEntry == null)
                    {
                        this.CurrentProc = new Procedure(this.Abort);
                        this.CurrentProc();
                        return;
                    }

                    if (txEntry.Status == TxStatus.Aborted)
                    {
                        // The tx holding the version has been aborted. Validation passed.
                        this.validateKeyList.Pop();
                        continue;
                    }
                    else if (txEntry.Status == TxStatus.Committed || txEntry.CommitTime >= 0)
                    {
                        if (this.commitTs > txEntry.CommitTime)
                        {
                            this.CurrentProc = new Procedure(this.Abort);
                            this.CurrentProc();
                            return;
                        }
                        else
                        {
                            this.validateKeyList.Pop();
                            continue;
                        }
                    }
                    else
                    {
                        UpdateCommitLowerBoundRequest updateCommitBoundReq = 
                            this.versionDb.EnqueueUpdateCommitLowerBound(this.txId, this.commitTs + 1);
                        this.requestStack.Push(updateCommitBoundReq);
                        return;
                    }
                }
                else if (this.requestStack.Count == 1 && this.requestStack.Peek() is UpdateCommitLowerBoundRequest)
                {
                    if (!this.requestStack.Peek().Finished)
                    {
                        // The pending request hasn't been processed. Returns the control to the caller.
                        return;
                    }

                    UpdateCommitLowerBoundRequest txCommitReq = this.requestStack.Pop() as UpdateCommitLowerBoundRequest;
                    long txCommitTs = txCommitReq.Result == null ? VersionDb.RETURN_ERROR_CODE : (long)txCommitReq.Result;

                    if (txCommitTs == VersionDb.RETURN_ERROR_CODE)
                    {
                        this.CurrentProc = new Procedure(this.Abort);
                        this.CurrentProc();
                        return;
                    }
                    else if (txCommitTs == TxTableEntry.DEFAULT_COMMIT_TIME)
                    {
                        // The tx who is locking the version has not had its commit timestamp.
                        this.validateKeyList.Pop();
                        continue;
                    }
                    else if (this.commitTs > txCommitTs)
                    {
                        this.CurrentProc = new Procedure(this.Abort);
                        this.CurrentProc();
                        return;
                    }
                    else if (this.commitTs <= txCommitTs)
                    {
                        this.validateKeyList.Pop();
                        continue;
                    }
                }
                else
                {
                    throw new TransactionException("An illegal state of tx validation.");
                }
            }

            // All versions pass validation. Move to the commit phase.
            this.validateKeyList = null;
            this.CurrentProc = new Procedure(this.WriteToLog);
            this.CurrentProc();
            return;
        }

        internal void WriteToLog()
        {
            if (this.requestStack.Count == 0)
            {
                UpdateTxStatusRequest updateTxReq = this.versionDb.EnqueueUpdateTxStatus(this.txId, TxStatus.Committed);
                this.requestStack.Push(updateTxReq);
                return;
            }
            else if (this.requestStack.Count == 1 && this.requestStack.Peek() is UpdateTxStatusRequest)
            {
                if (!this.requestStack.Peek().Finished)
                {
                    return;
                }

                this.requestStack.Pop();
                this.CurrentProc = new Procedure(this.PostProcessingAfterCommit);
                this.CurrentProc();
                return;
            }
        }

        internal void PostProcessingAfterCommit()
        {
            if (this.requestStack.Count == 0)
            {
                foreach (string tableId in this.commitSet.Keys)
                {
                    foreach (object recordKey in this.commitSet[tableId].Keys)
                    {
                        foreach (PostProcessingEntry entry in this.commitSet[tableId][recordKey])
                        {
                            ReplaceVersionRequest replaceVerReq = null;

                            if (entry.BeginTimestamp == TransactionExecution.UNSET_TX_COMMIT_TIMESTAMP)
                            {
                                replaceVerReq = this.versionDb.EnqueueReplaceVersionEntry(
                                    tableId,
                                    recordKey,
                                    entry.VersionKey,
                                    this.commitTs,
                                    entry.EndTimestamp,
                                    VersionEntry.EMPTY_TXID,
                                    this.txId,
                                    -1);
                            }
                            else
                            {
                                replaceVerReq = this.versionDb.EnqueueReplaceVersionEntry(
                                    tableId,
                                    recordKey,
                                    entry.VersionKey,
                                    entry.BeginTimestamp,
                                    this.commitTs,
                                    VersionEntry.EMPTY_TXID,
                                    this.txId,
                                    long.MaxValue);
                            }

                            this.requestStack.Push(replaceVerReq);
                        }
                    }
                }

                return;
            }
            else
            {
                foreach (TxRequest req in this.requestStack)
                {
                    if (!req.Finished)
                    {
                        return;
                    }
                }

                // All post-processing records have been uploaded.
                this.Progress = TxProgress.Close;
                this.CurrentProc = null;
                return;
            }
        }

        internal void Abort()
        {
            this.Progress = TxProgress.Final;

            if (this.requestStack.Count == 0)
            {
                UpdateTxStatusRequest updateTxReq = this.versionDb.EnqueueUpdateTxStatus(this.txId, TxStatus.Committed);
                this.requestStack.Push(updateTxReq);
                return;
            }
            else if (this.requestStack.Count == 1 && this.requestStack.Peek() is UpdateTxStatusRequest)
            {
                if (!this.requestStack.Peek().Finished)
                {
                    return;
                }

                foreach (string tableId in this.abortSet.Keys)
                {
                    foreach (object recordKey in this.abortSet[tableId].Keys)
                    {
                        foreach (PostProcessingEntry entry in this.abortSet[tableId][recordKey])
                        {
                            if (entry.BeginTimestamp == VersionEntry.DEFAULT_BEGIN_TIMESTAMP)
                            {
                                DeleteVersionRequest delVerReq = this.versionDb.EnqueueDeleteVersionEntry(
                                    tableId, recordKey, entry.VersionKey);
                                this.requestStack.Push(delVerReq);
                            }
                            else
                            {
                                ReplaceVersionRequest replaceVerReq = this.versionDb.EnqueueReplaceVersionEntry(
                                    tableId,
                                    recordKey,
                                    entry.VersionKey,
                                    entry.BeginTimestamp,
                                    entry.EndTimestamp,
                                    VersionEntry.EMPTY_TXID,
                                    this.txId,
                                    long.MaxValue);
                                this.requestStack.Push(replaceVerReq);
                            }
                        }
                    }
                }

                return;
            }
            else 
            {
                foreach (TxRequest req in this.requestStack)
                {
                    if (!req.Finished)
                    {
                        return;
                    }
                }

                // All pending records have been reverted.
                this.Progress = TxProgress.Close;
                this.CurrentProc = null;
                return;
            }
        }

        public void Insert(string tableId, object recordKey, object record)
        {
            // Checks whether the record is in the local write set
            if (this.writeSet.ContainsKey(tableId) &&
                this.writeSet[tableId].ContainsKey(recordKey))
            {
                if (this.writeSet[tableId][recordKey] != null)
                {
                    this.CurrentProc = new Procedure(this.Abort);
                    this.CurrentProc();
                    throw new TransactionException("Cannot insert the same record key twice.");
                }
                else
                {
                    this.writeSet[tableId][recordKey] = record;
                    return;
                }
            }

            // Checks whether the record is already in the local read set
            if (this.readSet.ContainsKey(tableId) &&
                this.readSet[tableId].ContainsKey(recordKey))
            {
                this.CurrentProc = new Procedure(this.Abort);
                this.CurrentProc();
                throw new TransactionException("The same record already exists.");
            }

            // Add the new record to local writeSet
            if (!this.writeSet.ContainsKey(tableId))
            {
                this.writeSet[tableId] = new Dictionary<object, object>();
            }

            this.writeSet[tableId][recordKey] = record;
        }

        public void Read(string tableId, object recordKey, out bool received, out object payload)
        {
            this.Read(tableId, recordKey, false, out received, out payload);
        }

        public void ReadAndInitialize(string tableId, object recordKey, out bool received, out object payload)
        {
            this.Read(tableId, recordKey, true, out received, out payload);
        }

        private void Read(string tableId, object recordKey, bool initi, out bool received, out object payload)
        {
            this.Progress = TxProgress.Read;

            received = false;
            payload = null;

            // try to find the record image in the local writeSet
            if (this.writeSet.ContainsKey(tableId) &&
                this.writeSet[tableId].ContainsKey(recordKey))
            {
                payload = this.writeSet[tableId][recordKey];
                received = true;
                return;
            }

            // try to find the obejct in the local readSet
            if (this.readSet.ContainsKey(tableId) &&
                this.readSet[tableId].ContainsKey(recordKey))
            {
                payload =  this.readSet[tableId][recordKey].Record;
                received = true;
                return;
            }

            if (this.readVersionList == null)
            {
                if (this.requestStack.Count == 0)
                {
                    if (initi)
                    {
                        InitiGetVersionListRequest initiGetVersionList = 
                            this.versionDb.EnqueueInitializeAndGetVersionList(tableId, recordKey);
                        this.requestStack.Push(initiGetVersionList);
                    }
                    else
                    {
                        GetVersionListRequest getVlistReq = this.versionDb.EnqueueGetVersionList(tableId, recordKey);
                        this.requestStack.Push(getVlistReq);
                    }
                    
                    return;
                }
                else if (this.requestStack.Count == 1 && (
                    this.requestStack.Peek() is GetVersionListRequest || 
                    this.requestStack.Peek() is InitiGetVersionListRequest))
                {
                    if (!this.requestStack.Peek().Finished)
                    {
                        return;
                    }

                    IEnumerable<VersionEntry> versionList = this.requestStack.Pop().Result as IEnumerable<VersionEntry>;
                    if (versionList == null)
                    {
                        received = true;
                        payload = null;
                        this.Progress = TxProgress.Open;
                        return;
                    }

                    this.readVersionList = new Queue<VersionEntry>(versionList.OrderByDescending(e => e.VersionKey));
                }
                else
                {
                    throw new TransactionException("An illegal state of tx read");
                }
            }

            VersionEntry visibleVersion = null;

            while (this.readVersionList.Count > 0)
            {
                if (this.requestStack.Count == 1 && this.requestStack.Peek() is GetTxEntryRequest)
                {
                    if (!this.requestStack.Peek().Finished)
                    {
                        return;
                    }

                    TxTableEntry pendingTx = this.requestStack.Pop().Result as TxTableEntry;
                    if (pendingTx == null)
                    {
                        // Failed to retrieve the status of the tx holding the version. 
                        // Moves on to the next version.
                        this.readVersionList.Dequeue();
                        continue;
                    }

                    VersionEntry versionEntry = this.readVersionList.Dequeue();

                    // If the version entry is a dirty write, skips the entry.
                    if (versionEntry.EndTimestamp == VersionEntry.DEFAULT_END_TIMESTAMP && 
                        (pendingTx.Status == TxStatus.Ongoing || pendingTx.Status == TxStatus.Aborted))
                    {
                        continue;
                    }

                    // A dirty write has been appended after this version entry. 
                    // This version is visible if the writing tx has not been committed 
                    if (versionEntry.EndTimestamp == long.MaxValue && pendingTx.Status != TxStatus.Committed)
                    {
                        visibleVersion = versionEntry;
                    }
                    // A dirty write is visible to this tx when the writing tx has been committed, 
                    // which has not finished postprocessing and changing the dirty write to a normal version entry
                    else if (versionEntry.EndTimestamp == VersionEntry.DEFAULT_END_TIMESTAMP && pendingTx.Status == TxStatus.Committed)
                    {
                        visibleVersion = new VersionEntry(
                            versionEntry.RecordKey,
                            versionEntry.VersionKey,
                            pendingTx.CommitTime,
                            long.MaxValue,
                            versionEntry.Record,
                            VersionEntry.EMPTY_TXID,
                            versionEntry.MaxCommitTs);
                    }

                    if (this.largestVersionKeyMap.ContainsKey(tableId))
                    {
                        if (this.largestVersionKeyMap[tableId].ContainsKey(recordKey))
                        {
                            this.largestVersionKeyMap[tableId][recordKey] = Math.Max(
                                this.largestVersionKeyMap[tableId][recordKey], versionEntry.VersionKey);
                        }
                        else
                        {
                            this.largestVersionKeyMap[tableId][recordKey] = versionEntry.VersionKey;
                        }
                    }
                    else
                    {
                        this.largestVersionKeyMap[tableId] = new Dictionary<object, long>();
                        this.largestVersionKeyMap[tableId][recordKey] = versionEntry.VersionKey;
                    }

                    if (visibleVersion != null)
                    {
                        break;
                    }
                }
                else if (this.requestStack.Count == 0)
                {
                    VersionEntry versionEntry = this.readVersionList.Peek();

                    if (versionEntry.TxId >= 0)
                    {
                        GetTxEntryRequest getTxReq = this.versionDb.EnqueueGetTxEntry(this.txId);
                        this.requestStack.Push(getTxReq);
                        return;
                    }
                    else
                    {
                        // When a tx has a begin timestamp after intialization
                        if (this.beginTimestamp >= 0 && 
                            this.beginTimestamp >= versionEntry.BeginTimestamp && 
                            this.beginTimestamp < versionEntry.EndTimestamp)
                        {
                            
                            visibleVersion = versionEntry;
                            break;
                        }
                        // When a tx has no begin timestamp after intialization, the tx is under serializability. 
                        // A read always returns the most-recently committed version.
                        else if (versionEntry.EndTimestamp == long.MaxValue)
                        {
                            visibleVersion = versionEntry;
                            break;
                        }
                        else
                        {
                            this.readVersionList.Dequeue();
                        }
                    }
                }
                else
                {
                    throw new TransactionException("An illegal state of tx read.");
                }
            }

            if (visibleVersion != null)
            {
                payload = visibleVersion.Record;

                // Add the record to local readSet
                if (!this.readSet.ContainsKey(tableId))
                {
                    this.readSet[tableId] = new Dictionary<object, ReadSetEntry>();
                }

                this.readSet[tableId][recordKey] = new ReadSetEntry(
                    visibleVersion.VersionKey,
                    visibleVersion.BeginTimestamp,
                    visibleVersion.EndTimestamp,
                    visibleVersion.TxId,
                    visibleVersion.Record);
            }

            this.readVersionList = null;
            this.Progress = TxProgress.Open;
            received = true;
        }

        public void Update(string tableId, object recordKey, object payload)
        {
            Debug.Assert(this.Progress == TxProgress.Open);

            if (this.writeSet.ContainsKey(tableId) &&
                this.writeSet[tableId].ContainsKey(recordKey))
            {
                if (this.writeSet[tableId][recordKey] != null)
                {
                    this.writeSet[tableId][recordKey] = payload;
                }
                // The record has been deleted by this tx. Cannot be updated. 
                else
                {
                    this.CurrentProc = new Procedure(this.Abort);
                    this.CurrentProc();
                    throw new TransactionException("The record to be updated has been deleted.");
                }
            }
            else if (this.readSet.ContainsKey(tableId) &&
                     this.readSet[tableId].ContainsKey(recordKey))
            {
                ReadSetEntry entry = this.readSet[tableId][recordKey];

                if (!this.writeSet.ContainsKey(tableId))
                {
                    this.writeSet[tableId] = new Dictionary<object, object>();
                }

                this.writeSet[tableId][recordKey] = payload;
            }
            else
            {
                this.CurrentProc = new Procedure(this.Abort);
                this.CurrentProc();
                throw new TransactionException("The record has not been read or does not exist. Cannot update it.");
            }
        }

        public void Delete(string tableId, object recordKey, out object payload)
        {
            payload = null;

            if (this.writeSet.ContainsKey(tableId) &&
                this.writeSet[tableId].ContainsKey(recordKey))
            {
                if (this.writeSet[tableId][recordKey] != null)
                {
                    payload = this.writeSet[tableId][recordKey];
                    this.writeSet[tableId][recordKey] = null;
                }
                else
                {
                    this.CurrentProc = new Procedure(this.Abort);
                    this.CurrentProc();
                    throw new TransactionException("The record to be deleted has been deleted by the same tx.");
                }
            }
            else if (this.readSet.ContainsKey(tableId) &&
                this.readSet[tableId].ContainsKey(recordKey))
            {
                payload = this.readSet[tableId][recordKey].Record;

                if (!this.writeSet.ContainsKey(tableId))
                {
                    this.writeSet[tableId] = new Dictionary<object, object>();
                }
                this.writeSet[tableId][recordKey] = null;
            }
            else
            {
                this.CurrentProc = new Procedure(this.Abort);
                this.CurrentProc();
                throw new TransactionException("The record has not been read or does not exist.Cannot delete it.");
            }
        }

        public void Commit()
        {
            Debug.Assert(this.Progress == TxProgress.Open);

            this.CurrentProc = new Procedure(this.Upload);
            this.CurrentProc();
            return;
        }
    }
}