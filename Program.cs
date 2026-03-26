using System;
using System.Collections.Generic;
using System.Threading;

namespace Lab3Pool
{
    public enum JobState
    {
        NoTask,
        Wait,
        Work,
        Done,
        Stop
    }

    public delegate int JobFunc(CancellationToken token);

    public sealed class MyQueue<TTask>
    {
        private readonly Queue<(uint Id, TTask Task)> _q = new Queue<(uint, TTask)>();
        private readonly ReaderWriterLockSlim _qLock = new ReaderWriterLockSlim();
        private readonly ReaderWriterLockSlim _statLock = new ReaderWriterLockSlim();
        private readonly Dictionary<uint, JobState> _statusMap = new Dictionary<uint, JobState>();

        private uint _nextId;

        public bool IsEmpty()
        {
            _qLock.EnterReadLock();
            try
            {
                return _q.Count == 0;
            }
            finally
            {
                _qLock.ExitReadLock();
            }
        }

        public uint Push(TTask task)
        {
            _qLock.EnterWriteLock();
            try
            {
                _nextId++;
                _q.Enqueue((_nextId, task));
                AddState(_nextId);
                return _nextId;
            }
            finally
            {
                _qLock.ExitWriteLock();
            }
        }

        public bool Pop(out uint id, out TTask task)
        {
            _qLock.EnterWriteLock();
            try
            {
                if (_q.Count == 0)
                {
                    id = 0;
                    task = default!;
                    return false;
                }

                (id, task) = _q.Dequeue();
                return true;
            }
            finally
            {
                _qLock.ExitWriteLock();
            }
        }

        public bool AddState(uint id)
        {
            _statLock.EnterWriteLock();
            try
            {
                if (_statusMap.ContainsKey(id))
                {
                    return false;
                }

                _statusMap[id] = JobState.Wait;
                Console.WriteLine($"{id}: Wait");
                return true;
            }
            finally
            {
                _statLock.ExitWriteLock();
            }
        }

        public void SetState(uint id, JobState status)
        {
            _statLock.EnterWriteLock();
            try
            {
                if (_statusMap.ContainsKey(id))
                {
                    _statusMap[id] = status;
                    Console.WriteLine($"{id}: {status}");
                }
            }
            finally
            {
                _statLock.ExitWriteLock();
            }
        }

        public JobState GetState(uint id)
        {
            _statLock.EnterReadLock();
            try
            {
                return _statusMap.TryGetValue(id, out JobState status)
                    ? status
                    : JobState.NoTask;
            }
            finally
            {
                _statLock.ExitReadLock();
            }
        }
    }

    public sealed class MyPool
    {
        private readonly object _sync = new object();
        private readonly List<Thread> _threads = new List<Thread>();
        private readonly MyQueue<JobFunc> _q = new MyQueue<JobFunc>();

        private bool _isInit;
        private int _thrCount;

        public void StartPool(int thrCount)
        {
            lock (_sync)
            {
                if (_isInit || _threads.Count > 0)
                {
                    return;
                }

                for (int i = 0; i < thrCount; i++)
                {
                    Thread t = new Thread(WorkLoop)
                    {
                        IsBackground = true,
                        Name = $"Worker-{i + 1}"
                    };

                    _threads.Add(t);
                }

                _thrCount = _threads.Count;
                _isInit = _threads.Count > 0;

                foreach (Thread t in _threads)
                {
                    t.Start();
                }
            }
        }

        public uint PushTask(JobFunc task)
        {
            lock (_sync)
            {
                if (!_isInit)
                {
                    return 0;
                }

                uint id = _q.Push(task);
                Monitor.Pulse(_sync);
                return id;
            }
        }

        public JobState StateById(uint id)
        {
            return _q.GetState(id);
        }

        public int ThreadCount()
        {
            return _thrCount;
        }

        private void WorkLoop()
        {
            while (true)
            {
                JobFunc job;
                uint id;

                lock (_sync)
                {
                    while (!_q.Pop(out id, out job))
                    {
                        Monitor.Wait(_sync);
                    }
                }

                _q.SetState(id, JobState.Work);
                job(CancellationToken.None);
                _q.SetState(id, JobState.Done);
            }
        }
    }

    internal static class Program
    {
        private static readonly ThreadLocal<Random> Rnd =
            new ThreadLocal<Random>(() => new Random(Guid.NewGuid().GetHashCode()));

        private static int FakeTask(CancellationToken token)
        {
            int sec = Rnd.Value!.Next(3, 7);
            Thread.Sleep(sec * 1000);
            return sec;
        }

        private static void Main()
        {
            MyPool pool = new MyPool();
            pool.StartPool(4);

            for (int i = 0; i < 8; i++)
            {
                uint id = pool.PushTask(FakeTask);
                Console.WriteLine($"Task {id} added.");
                Thread.Sleep(500);
            }

            Console.WriteLine($"Threads: {pool.ThreadCount()}");
            Thread.Sleep(20000);
        }
    }
}
