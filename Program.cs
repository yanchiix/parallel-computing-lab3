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
        private uint _opCount;
        private uint _lenSum;

        public int Count()
        {
            _qLock.EnterReadLock();
            try
            {
                return _q.Count;
            }
            finally
            {
                _qLock.ExitReadLock();
            }
        }

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

        public double AvgLen()
        {
            _qLock.EnterReadLock();
            try
            {
                if (_opCount == 0)
                {
                    return 0.0;
                }

                return Math.Round((double)_lenSum / _opCount, 2);
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
                _opCount++;
                _lenSum += (uint)_q.Count;
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

                _opCount++;
                _lenSum += (uint)_q.Count;

                (id, task) = _q.Dequeue();
                return true;
            }
            finally
            {
                _qLock.ExitWriteLock();
            }
        }

        public List<uint> TakeAllIds()
        {
            List<uint> ids = new List<uint>();

            _qLock.EnterWriteLock();
            try
            {
                while (_q.Count > 0)
                {
                    var item = _q.Dequeue();
                    ids.Add(item.Id);
                }
            }
            finally
            {
                _qLock.ExitWriteLock();
            }

            return ids;
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

    public sealed class MyPool : IDisposable
    {
        private readonly object _sync = new object();
        private readonly List<Thread> _threads = new List<Thread>();
        private readonly MyQueue<JobFunc> _q = new MyQueue<JobFunc>();
        private readonly Dictionary<uint, int> _ans = new Dictionary<uint, int>();
        private readonly ReaderWriterLockSlim _ansLock = new ReaderWriterLockSlim();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private bool _isInit;
        private bool _stopAfterAll;
        private bool _stopNow;
        private bool _isPaused;

        private long _idleTicks;
        private long _jobTicks;
        private int _cycles;
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

        public bool Working()
        {
            lock (_sync)
            {
                return _isInit && !_stopAfterAll && !_stopNow;
            }
        }

        public void PausePool()
        {
            lock (_sync)
            {
                if (_isInit && !_stopAfterAll && !_stopNow)
                {
                    _isPaused = true;
                }
            }
        }

        public void ContinuePool()
        {
            lock (_sync)
            {
                _isPaused = false;
                Monitor.PulseAll(_sync);
            }
        }

        public uint PushTask(JobFunc task)
        {
            lock (_sync)
            {
                if (!_isInit || _stopAfterAll || _stopNow)
                {
                    return 0;
                }

                uint id = _q.Push(task);
                Monitor.Pulse(_sync);
                return id;
            }
        }

        public void StopAfterQueue()
        {
            lock (_sync)
            {
                if (!_isInit)
                {
                    return;
                }

                _stopAfterAll = true;
                _isPaused = false;
                Monitor.PulseAll(_sync);
            }

            WaitThreads();
            ClearFlags();
        }

        public void StopFast()
        {
            List<uint> leftIds;

            lock (_sync)
            {
                if (!_isInit)
                {
                    return;
                }

                _stopNow = true;
                _isPaused = false;
                _cts.Cancel();
                leftIds = _q.TakeAllIds();

                foreach (uint id in leftIds)
                {
                    _q.SetState(id, JobState.Stop);
                }

                Monitor.PulseAll(_sync);
            }

            WaitThreads();
            ClearFlags();
        }

        public JobState StateById(uint id)
        {
            return _q.GetState(id);
        }

        public int TaskAns(uint id)
        {
            _ansLock.EnterReadLock();
            try
            {
                if (!_ans.TryGetValue(id, out int res))
                {
                    throw new InvalidOperationException("Результат для вказаного ID відсутній.");
                }

                return res;
            }
            finally
            {
                _ansLock.ExitReadLock();
            }
        }

        public double AvgQueue()
        {
            return _q.AvgLen();
        }

        public int ThreadCount()
        {
            return _thrCount;
        }

        public void ShowStat()
        {
            Console.WriteLine();
            Console.WriteLine("===== STATISTICS =====");
            Console.WriteLine($"Created threads: {ThreadCount()}");
            Console.WriteLine($"Average queue length: {AvgQueue():F2}");

            if (_cycles > 0)
            {
                double waitMs = (double)_idleTicks / _cycles / TimeSpan.TicksPerMillisecond;
                double workMs = (double)_jobTicks / _cycles / TimeSpan.TicksPerMillisecond;

                Console.WriteLine($"Average thread wait time: {waitMs:F2} ms");
                Console.WriteLine($"Average task execution time: {workMs:F2} ms");
            }
            else
            {
                Console.WriteLine("No jobs were processed.");
            }

            Console.WriteLine("======================");
            Console.WriteLine();
        }

        private void WorkLoop()
        {
            while (true)
            {
                JobFunc job;
                uint id;
                long waitBeg = DateTime.UtcNow.Ticks;

                lock (_sync)
                {
                    while (true)
                    {
                        if (_stopNow)
                        {
                            return;
                        }

                        if (!_isPaused && _q.Pop(out id, out job))
                        {
                            break;
                        }

                        if (_stopAfterAll && _q.IsEmpty())
                        {
                            return;
                        }

                        Monitor.Wait(_sync);
                    }
                }

                long waitEnd = DateTime.UtcNow.Ticks;
                Interlocked.Add(ref _idleTicks, waitEnd - waitBeg);

                _q.SetState(id, JobState.Work);

                long workBeg = DateTime.UtcNow.Ticks;
                try
                {
                    int res = job(_cts.Token);
                    long workEnd = DateTime.UtcNow.Ticks;

                    Interlocked.Add(ref _jobTicks, workEnd - workBeg);

                    _ansLock.EnterWriteLock();
                    try
                    {
                        _ans[id] = res;
                    }
                    finally
                    {
                        _ansLock.ExitWriteLock();
                    }

                    _q.SetState(id, JobState.Done);
                }
                catch (OperationCanceledException)
                {
                    long workEnd = DateTime.UtcNow.Ticks;
                    Interlocked.Add(ref _jobTicks, workEnd - workBeg);
                    _q.SetState(id, JobState.Stop);
                }
                finally
                {
                    Interlocked.Increment(ref _cycles);
                }
            }
        }

        private void WaitThreads()
        {
            foreach (Thread t in _threads)
            {
                if (t.IsAlive)
                {
                    t.Join();
                }
            }
        }

        private void ClearFlags()
        {
            lock (_sync)
            {
                _threads.Clear();
                _isInit = false;
                _isPaused = false;
                _stopAfterAll = false;
                _stopNow = false;
            }
        }

        public void Dispose()
        {
            StopAfterQueue();
            _cts.Dispose();
            _ansLock.Dispose();
        }
    }

    internal static class Program
    {
        private static readonly ThreadLocal<Random> Rnd =
            new ThreadLocal<Random>(() => new Random(Guid.NewGuid().GetHashCode()));

        private static int FakeTask(CancellationToken token)
        {
            int sec = Rnd.Value!.Next(5, 11);
            int allMs = sec * 1000;
            int curMs = 0;

            while (curMs < allMs)
            {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(100);
                curMs += 100;
            }

            return sec;
        }

        private static void AddTasks(object? obj)
        {
            if (obj is not MyPool pool)
            {
                return;
            }

            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(Rnd.Value!.Next(1, 6) * 1000);
                uint id = pool.PushTask(FakeTask);
                if (id != 0)
                {
                    Console.WriteLine($"Task {id} added.");
                }
            }
        }

        private static void Main()
        {
            MyPool pool = new MyPool();
            pool.StartPool(4);

            Thread prod1 = new Thread(AddTasks);
            Thread prod2 = new Thread(AddTasks);

            prod1.Start(pool);
            prod2.Start(pool);

            // демонстрація паузи / відновлення
            Thread.Sleep(7000);
            Console.WriteLine("\nPOOL PAUSED\n");
            pool.PausePool();

            Thread.Sleep(4000);
            Console.WriteLine("\nPOOL RESUMED\n");
            pool.ContinuePool();

            prod1.Join();
            prod2.Join();

            // коректне завершення після виконання всіх задач
            pool.StopAfterQueue();

            // для перевірки режиму миттєвого завершення
            // pool.StopFast();

            pool.ShowStat();
        }
    }
}
