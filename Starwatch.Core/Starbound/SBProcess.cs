using Starwatch.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Starwatch.Starbound
{
    public class SBProcess : IDisposable
    {
        public static readonly Regex TagRegex = new Regex(@"(\[[nrofaIWE]{4,5}\].*)", RegexOptions.Compiled);
        private static long SbCounter = 0;

        public Logger Logger { get; }

        public ProcessStartInfo StartInfo { get; }
        private Process _process;

        private SemaphoreSlim _queueSemaphore;
        private SemaphoreSlim _threadSemaphore;
        private Queue<string> _queue;
        private Thread _thread;

        private volatile State state = State.Offline;
        private enum State : int { Offline = 0, Killing = 1, Exiting = 2, Starting = 3, Running = 4, Aborting = 5 }

        public bool IsOffline => state == State.Offline;

        public event Action Exited;

        public SBProcess(string file, string directory, Logger logger, int capacity = 100)
        {
            this.StartInfo = new ProcessStartInfo()
            {
                FileName = file,
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            this.Logger = logger;
            this._queueSemaphore = new SemaphoreSlim(1, 1);
            this._threadSemaphore = new SemaphoreSlim(1, 1);
            this._queue = new Queue<string>(capacity);
        }

        public MemoryUsage GetMemoryUsage() { return new MemoryUsage(_process); }

        /// <summary>Starts the process</summary>
        public void Start()
        {
            if (_thread != null)
                throw new InvalidOperationException("Cannot start the process if it is already running.");

            Log("Starting Thread");
            _thread = new Thread(p_RunThread);
            _thread.Name = "SBProcess " + (SbCounter++);
            _thread.Start();
        }

        /// <summary>Stops the process</summary>
        public void Stop()
        {
            Log("Aborting State");
            state = State.Aborting;
            p_KillProcess();
        }

        /// <summary>Stops the process and asynchronously waits for it to finish cleanup.</summary>
        /// <returns></returns>
        public async Task StopAsync()
        {
            Stop();
            Log("Waiting for thread to cleanup.");
            await this._threadSemaphore.WaitAsync();
            this._threadSemaphore.Release();
        }

        /// <summary>
        /// Dequeues all the content that has been read by the stream so far.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<string[]> ReadStandardOutputAsync()
        {
            await this._queueSemaphore.WaitAsync();
            try
            {
                List<string> tmp = new List<string>(_queue.Count);
                while (_queue.Count > 0) tmp.Add(_queue.Dequeue());
                return tmp.ToArray();
            }
            finally
            {
                this._queueSemaphore.Release();
            }
        }

        private void p_RunThread()
        {
            this._threadSemaphore.Wait();
            try
            {
                // Start the process
                p_StartProcess();

                // Non-blocking output reading
                Task.Run(() =>
                {
                    while (state == State.Running && _process != null && !_process.HasExited)
                    {
                        try
                        {
                            string line = _process.StandardOutput.ReadLine();
                            if (!string.IsNullOrEmpty(line))
                            {
                                p_EnqueueContent(line);
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // This can occur if the process has already exited, so we just break out.
                            break;
                        }
                    }
                });

                // Monitor process state
                while (state == State.Running && _process != null)
                {
                    if (_process.WaitForExit(100))
                    {
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                // An exception has occurred
                LogError(e);
            }
            finally
            {
                // Finally kill the process, then release our semaphore.
                Log("Exited Read Loop, aborting and releasing semaphore");
                p_KillProcess();
                this._threadSemaphore.Release();
            }
        }

        private void p_EnqueueContent(string content)
        {
            // Wait for our turn to add more content
            this._queueSemaphore.Wait();
            try
            {
                // Split the logs up, adding each one 
                foreach (var p in TagRegex.Split(content))
                {
                    // Regex gives us empties?
                    string str = p.Trim().Replace("\r", "\\r").Replace("\n", "\\n");
                    if (str.Length > 0)
                        this._queue.Enqueue(str);
                }
            }
            finally
            {
                // Finally release the queue
                this._queueSemaphore.Release();
            }
        }

        private bool p_StartProcess()
        {
            Log("Starting Process...");
            if (_process != null)
                throw new InvalidOperationException("Cannot start the process while it is already running.");

            // Update the state
            state = State.Starting;

            // Create the process and hook into our events
            Log("Creating Process...");
            _process = new Process()
            {
                EnableRaisingEvents = true,
                StartInfo = StartInfo
            };

            _process.Exited += ProcessExited;

            // Start the process
            Log("Starting Process...");
            var success = _process.Start();
            if (!success)
            {
                Log("Failed, cleaning up...");
                p_KillProcess();
                return false;
            }
            else
            {
                Log("Success");
                state = State.Running;
                return true;
            }
        }

        private void ProcessExited(object sender, EventArgs e)
        {
            Log("Process has exited.");

            // We are already killing the process, abort!
            if (state == State.Killing)
                return;

            // Actually kill the process
            Log("Exiting Process ourselves...");
            state = State.Exiting;
            p_KillProcess();
        }

        private bool p_KillProcess()
        {
            if (_process == null)
                return true;

            if (state == State.Killing)
                return false;

            // Update the state
            bool isExiting = state == State.Exiting;
            state = State.Killing;

            try
            {
                // Flush and drain the output
                Log("Flushing and draining output before killing process...");
                while (!_process.HasExited && !_process.StandardOutput.EndOfStream)
                {
                    string output = _process.StandardOutput.ReadLine();
                    if (!string.IsNullOrEmpty(output))
                    {
                        p_EnqueueContent(output);
                    }
                }

                // Kill the process if it hasn't exited
                if (!_process.HasExited && !isExiting)
                {
                    Log("Killing Process");
                    try
                    {
                        _process.Kill();
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        LogError(ex, "Failed to kill: {0}");
                    }

                    // Wait for the process to exit, with a timeout
                    Log("Waiting for exit");
                    if (!_process.WaitForExit(5000)) // 5 seconds timeout
                    {
                        Log("Process did not exit in time, forcing termination.");
                        _process.Kill();
                    }
                }
            }
            catch (System.InvalidOperationException e)
            {
                LogError(e, "IOE: {0}");
            }
            catch (Exception ex)
            {
                LogError(ex, "Unexpected error during process kill");
            }

            // Dispose of the process
            Log("Disposing and cleaning up process");
            _process?.Dispose();
            _process = null;

            Log("Killing finished");
            state = State.Offline;

            Log("Invoking On Exit");
            Exited?.Invoke();
            return true;
        }

        #region Logging
        private void LogError(Exception e, string format = "ERRO: {0}") { Logger.LogError(e, format);  }
        private void Log(string log) { Logger.Log(log); }

        /// <summary>
        /// Disposes the object.
        /// </summary>
        public void Dispose()
        {
            Stop();
            _queueSemaphore.Dispose();
            _threadSemaphore.Dispose();
        }
        #endregion
    }
}
