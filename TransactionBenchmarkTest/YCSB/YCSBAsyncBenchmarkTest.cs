﻿namespace TransactionBenchmarkTest.YCSB
{
    using GraphView.Transaction;
    using ServiceStack.Redis;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;

    class YCSBAsyncBenchmarkTest
    {
        public static readonly String TABLE_ID = "ycsb_table";

        public static readonly long REDIS_DB_INDEX = 7L;

        public static bool RESHUFFLE = true;

        public static Func<object, object> ACTION = (object obj) =>
        {
            Tuple<VersionDb, TxWorkload> tuple = (Tuple<VersionDb, TxWorkload>)obj;

            TxWorkload workload = tuple.Item2;
            VersionDb versionDb = tuple.Item1;

            Transaction tx = new Transaction(null, versionDb);
            string readValue = null;
            switch (workload.Type)
            {
                case "READ":
                    readValue = (string)tx.Read(workload.TableId, workload.Key);
                    break;

                case "UPDATE":
                    readValue = (string)tx.Read(workload.TableId, workload.Key);
                    if (readValue != null)
                    {
                        tx.Update(workload.TableId, workload.Key, workload.Value);
                    }
                    break;

                case "DELETE":
                    readValue = (string)tx.Read(workload.TableId, workload.Key);
                    if (readValue != null)
                    {
                        tx.Delete(workload.TableId, workload.Key);
                    }
                    break;

                case "INSERT":
                    readValue = (string)tx.ReadAndInitialize(workload.TableId, workload.Key);
                    if (readValue == null)
                    {
                        tx.Insert(workload.TableId, workload.Key, workload.Value);
                    }
                    break;

                default:
                    break;
            }
            return true;
        };

        private List<TransactionExecutor> executorList;

        /// <summary>
        /// The number of executors
        /// </summary>
        private int executorCount;

        /// <summary>
        /// The number of tx count per executor
        /// </summary>
        private int txCountPerExecutor;

        /// <summary>
        /// The exact ticks when the test starts
        /// </summary>
        private long testBeginTicks;

        /// <summary>
        /// The exact ticks when then test ends
        /// </summary>
        private long testEndTicks;

        /// <summary>
        /// total redis commands processed
        /// </summary>
        private long commandCount = 0;

        /// <summary>
        /// the version db instance
        /// </summary>
        private VersionDb versionDb;

        /// <summary>
        /// the number of records need to be loaded
        /// </summary>
        private int recordCount;

        /// <summary>
        /// the total number of tasks
        /// </summary>
        private int totalTasks = 0;

        /// <summary>
        /// the tables should be flushed
        /// </summary>
        private string[] tables;

        internal int TxThroughput
        {
            get
            {
                double runSeconds = this.RunSeconds;
                return (int)(this.totalTasks / runSeconds);
            }
        }

        internal double RunSeconds
        {
            get
            {
                return ((this.testEndTicks - this.testBeginTicks) * 1.0) / 10000000;
            }
        }

        internal double RedisThroughput
        {
            get
            {
                double runSeconds = this.RunSeconds;
                return (int)(this.commandCount / runSeconds);
            }
        }

        public YCSBAsyncBenchmarkTest(
            int recordCount,
            int executorCount,
            int txCountPerExecutor,
            VersionDb versionDb,
            string[] flushTables = null)
        {
            this.versionDb = versionDb;
            this.recordCount = recordCount;
            this.executorCount = executorCount;
            this.txCountPerExecutor = txCountPerExecutor;
            this.executorList = new List<TransactionExecutor>();
            this.totalTasks = 0;
            this.tables = flushTables;
        }

        internal void Setup(string dataFile, string operationFile)
        {
            // step1: flush the database
            this.versionDb.Clear();
            Console.WriteLine("Flushed the database");

            // step2: create version table
            this.versionDb.CreateVersionTable(TABLE_ID, REDIS_DB_INDEX);

            // step3: load data
            this.loadDataParallely(dataFile);
            // this.LoadDataSequentially(dataFile);
            SingletonPartitionedVersionDb.EnqueuedRequests = 0;

            // step 4: fill workers' queue
            if (this.versionDb is SingletonPartitionedVersionDb && RESHUFFLE)
            {
                this.executorList = this.ReshuffleFillWorkerQueue(operationFile, this.executorCount, this.executorCount * this.txCountPerExecutor);
            }
            else
            {
                this.FillWorkerQueue(operationFile);
            }
        }

        internal void Run()
        {
            TransactionExecution.TEST = true;
            YCSBStoredProcedure.ONLY_CLOSE = false;
            Console.WriteLine("Try to run {0} tasks in {1} workers", (this.executorCount * this.txCountPerExecutor), this.executorCount);
            Console.WriteLine("Running......");

            long commandCountBeforeRun = 0;
            if (this.versionDb is RedisVersionDb)
            {
                commandCountBeforeRun = this.GetCurrentCommandCount();
            }

            this.testBeginTicks = DateTime.Now.Ticks;
            List<Thread> threadList = new List<Thread>();

            foreach (TransactionExecutor executor in this.executorList)
            {
                Thread thread = new Thread(new ThreadStart(executor.Execute2));
                threadList.Add(thread);
                thread.Start();
            }

            int finishedTasks = 0;
            while (true)
            {
                // check whether all tasks finished every 100 ms
                Thread.Sleep(10);
                finishedTasks = 0;
                bool allFinished = true;

                foreach (TransactionExecutor executor in this.executorList)
                {
                    finishedTasks += executor.FinishedTxs;
                    if (!executor.AllRequestsFinished)
                    {
                        allFinished = false;
                        // Console.WriteLine(executor.FinishedTxs);
                    }
                    // Console.WriteLine(executorList[1].FinishedTxs);
                }
                Console.WriteLine("Execute {0} Tasks", finishedTasks);

                // Shutdown all workers
                if (allFinished)
                {
                    this.testEndTicks = DateTime.Now.Ticks;
                    foreach (TransactionExecutor executor in this.executorList)
                    {
                        executor.Active = false;
                    }
                    break;
                }
            }

            if (this.versionDb is RedisVersionDb)
            {
                long commandCountAfterRun = this.GetCurrentCommandCount();
                this.commandCount = commandCountAfterRun - commandCountBeforeRun;
            }

            Console.WriteLine("Finished all tasks");
        }

        internal void Stats()
        {
            this.totalTasks = this.executorCount * this.txCountPerExecutor;
            Console.WriteLine("\nFinshed {0} requests in {1} seconds", (this.executorCount * this.txCountPerExecutor), this.RunSeconds);
            Console.WriteLine("Transaction Throughput: {0} tx/second", this.TxThroughput);

            int totalTxs = 0, abortedTxs = 0;
            foreach (TransactionExecutor executor in this.executorList)
            {
                totalTxs += executor.FinishedTxs;
                abortedTxs += (executor.FinishedTxs - executor.CommittedTxs);
            }
            Console.WriteLine("\nFinshed {0} txs, Aborted {1} txs", totalTxs, abortedTxs);
            Console.WriteLine("Transaction AbortRate: {0}%", (abortedTxs * 1.0 / totalTxs) * 100);

            if (this.versionDb is RedisVersionDb)
            {
                Console.WriteLine("\nFinshed {0} commands in {1} seconds", this.commandCount, this.RunSeconds);
                Console.WriteLine("Redis Throughput: {0} cmds/second", this.RedisThroughput);
            }

            Console.WriteLine();
        }

        /// <summary>
        /// Load record by multiple threads
        /// </summary>
        /// <param name="dataFile"></param>
        private void loadDataParallely(string dataFile)
        {
            long beginTicks = DateTime.Now.Ticks;
            // 3.1 compute the number of partitions
            int partitions = 0;
            if (this.versionDb is RedisVersionDb)
            {
                partitions = ((RedisVersionDb)this.versionDb).RedisManager.RedisInstanceCount;
            }
            else if (this.versionDb is SingletonPartitionedVersionDb)
            {
                partitions = ((SingletonPartitionedVersionDb)this.versionDb).PartitionCount;
            }

            // 3.2 Load in multiple workers
            List<TransactionExecutor> executors =
                this.ReshuffleFillWorkerQueue(dataFile, partitions, this.recordCount);

            // 3.4 load records
            List<Thread> threadList = new List<Thread>();
            foreach (TransactionExecutor executor in executors)
            {
                Thread thread = new Thread(new ThreadStart(executor.Execute2));
                threadList.Add(thread);
                thread.Start();
            }

            int loaded = 0, times = 0;
            while (true)
            {
                // check whether all tasks finished every 100 ms
                Thread.Sleep(100);
                times++;

                bool allFinished = true;
                loaded = 0;
                foreach (TransactionExecutor executor in executors)
                {
                    if (!executor.AllRequestsFinished)
                    {
                        allFinished = false;
                    }
                    loaded += executor.FinishedTxs;
                }

                Console.WriteLine("Loaded {0} records", loaded);

                // Console.WriteLine("Loaded {0} records", loaded);
                // Shutdown all workers
                if (allFinished)
                {
                    foreach (TransactionExecutor executor in executors)
                    {
                        executor.Active = false;
                    }
                    break;
                }
            }

            long endTicks = DateTime.Now.Ticks;

            //int loaded = 0;
            Console.WriteLine("Load records successfully, {0} records in total in {1} seconds",
                loaded, (endTicks - beginTicks) * 1.0 / 10000000);
            Console.WriteLine("END");
        }

        private void LoadDataSequentially(string dataFile)
        {
            using (StreamReader reader = new StreamReader(dataFile))
            {
                string line;
                int count = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] fields = this.ParseCommandFormat(line);
                    TxWorkload workload = new TxWorkload(fields[0], TABLE_ID, fields[2], fields[3]);
                    count++;

                    ACTION(Tuple.Create(this.versionDb, workload));
                    if (count % 10000 == 0)
                    {
                        Console.WriteLine("Loaded {0} records", count);
                        if (count == 1000000)
                        {
                            break;
                        }
                    }
                }
                Console.WriteLine("Load records successfully, {0} records in total", count);
            }
        }

        private void FillWorkerQueue(string operationFile)
        {
            using (StreamReader reader = new StreamReader(operationFile))
            {
                string line;
                int instanceIndex = 0;
                for (int i = 0; i < this.executorCount; i++)
                {
                    //line = reader.ReadLine();
                    //string[] fields = this.ParseCommandFormat(line);
                    Queue<TransactionRequest> reqQueue = new Queue<TransactionRequest>();
                    for (int j = 0; j < this.txCountPerExecutor; j++)
                    {
                        line = reader.ReadLine();
                        string[] fields = this.ParseCommandFormat(line);

                        TxWorkload workload = new TxWorkload(fields[0], TABLE_ID, fields[2], fields[3]);
                        // TxWorkload workload = new TxWorkload("CLOSE", TABLE_ID, fields[2], fields[3]);
                        string sessionId = ((i * this.txCountPerExecutor) + j + 1).ToString();
                        YCSBStoredProcedure procedure = new YCSBStoredProcedure(sessionId, workload);
                        TransactionRequest req = new TransactionRequest(sessionId, procedure);
                        reqQueue.Enqueue(req);
                    }

                    Console.WriteLine("Filled {0} executors", i + 1);

                    this.totalTasks += reqQueue.Count;
                    this.executorList.Add(new TransactionExecutor(this.versionDb, null, reqQueue, i, i, 0,
                        this.versionDb.GetResourceManagerByPartitionIndex(i), tables));
                }
            }
        }

        // Reshuffle the workloads and make sure all workloads in the same executor
        // will have the same partition key
        private List<TransactionExecutor> ReshuffleFillWorkerQueue(string operationFile, int executorCount, int totalWorkloads)
        {
            // new transaction queues at first
            Queue<TransactionRequest>[] queueArray = new Queue<TransactionRequest>[executorCount];
            List<TransactionExecutor> executors = new List<TransactionExecutor>(executorCount);

            for (int i = 0; i < executorCount; i++)
            {
                queueArray[i] = new Queue<TransactionRequest>();
            }

            using (StreamReader reader = new StreamReader(operationFile))
            {
                string line;
                for (int i = 0; i < totalWorkloads; i++)
                {
                    line = reader.ReadLine();
                    string[] fields = this.ParseCommandFormat(line);

                    TxWorkload workload = new TxWorkload(fields[0], TABLE_ID, fields[2], fields[3]);
                    string sessionId = (i + 1).ToString();
                    YCSBStoredProcedure procedure = new YCSBStoredProcedure(sessionId, workload);
                    TransactionRequest req = new TransactionRequest(sessionId, procedure);

                    int pk = this.versionDb.PhysicalPartitionByKey(fields[2]);
                    queueArray[pk].Enqueue(req);
                }
            }

            for (int pk = 0; pk < executorCount; pk++)
            {
                Queue<TransactionRequest> txQueue = queueArray[pk];
                TxResourceManager manager = this.versionDb.GetResourceManagerByPartitionIndex(pk);

                executors.Add(
                    new TransactionExecutor(this.versionDb, null, txQueue, pk, pk, 0, manager, tables));

                Console.WriteLine("Executor {0} workloads count: {1}", pk, txQueue.Count);
            }

            return executors;
        }

        private string[] ParseCommandFormat(string line)
        {
            string[] fields = line.Split(' ');
            string value = null;
            int fieldsOffset = fields[0].Length + fields[1].Length + fields[2].Length + 3 + 9;
            int fieldsEnd = line.Length - 2;

            if (fieldsOffset < fieldsEnd)
            {
                value = line.Substring(fieldsOffset, fieldsEnd - fieldsOffset + 1);
            }

            return new string[] {
                fields[0], fields[1], fields[2], value
            };
        }

        /// <summary>
        /// Here there is a bug in ServiceStack.Redis, it will not refresh the info aftering reget a client from
        /// the pool, which means the number of commands will be not changed.
        /// 
        /// The bug has been fixed in Version 4.0.58 (Commerical Version), our reference version is Version 3.9 (The last open source version).
        /// 
        /// So here we have to dispose the redis client manager and reconnect with redis to get the lastest commands.
        /// </summary>
        /// <returns></returns>
        private long GetCurrentCommandCount()
        {
            RedisVersionDb redisVersionDb = (RedisVersionDb)this.versionDb;
            RedisClientManager manager = redisVersionDb.RedisManager;

            long commandCount = 0;
            for (int i = 0; i < manager.RedisInstanceCount; i++)
            {
                using (RedisClient redisClient = manager.GetLastestClient(0, 0))
                {
                    string countStr = redisClient.Info["total_commands_processed"];
                    long count = Convert.ToInt64(countStr);
                    commandCount += count;
                }
            }
            return commandCount;
        }
    }
}