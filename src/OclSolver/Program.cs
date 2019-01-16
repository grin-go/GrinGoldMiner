using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CudaSolver;
using OpenCl.DotNetCore.CommandQueues;
using OpenCl.DotNetCore.Contexts;
using OpenCl.DotNetCore.Devices;
using OpenCl.DotNetCore.Interop.CommandQueues;
using OpenCl.DotNetCore.Kernels;
using OpenCl.DotNetCore.Memory;
using OpenCl.DotNetCore.Platforms;
using SharedSerialization;

namespace OclSolver
{
    //https://www.reddit.com/r/MoneroMining/comments/8dhtrv/anybody_successfully_set_up_xmrstak_on_linux/
    // dotnet publish -c Release -r win-x64
    //dotnet restore -s https://dotnet.myget.org/F/dotnet-core/api/v3/index.json
    //sudo apt-get install dotnet-sdk-2.2 clang-3.9 libkrb5-dev zlib1g-dev  libcurl4-gnutls-dev

    class Program
    {
        const long DUCK_SIZE_A = 129; // AMD 126 + 3
        const long DUCK_SIZE_B = 83;
        const long BUFFER_SIZE_A1 = DUCK_SIZE_A * 1024 * (4096 - 128) * 2;
        const long BUFFER_SIZE_A2 = DUCK_SIZE_A * 1024 * 256 * 2;
        const long BUFFER_SIZE_B = DUCK_SIZE_B * 1024 * 4096 * 2;
        const long BUFFER_SIZE_U32 = (DUCK_SIZE_A + DUCK_SIZE_B) * 1024 * 4096 * 2;

        const long INDEX_SIZE = 256 * 256 * 4;

        // set this in dry debug runs
        static int platformID = 0;
        static int deviceID = 0;

        static int port = 13500;
        static bool TEST = false;

        static MemoryBuffer bufferA1;
        static MemoryBuffer bufferA2;
        static MemoryBuffer bufferB;
        static MemoryBuffer bufferI1;
        static MemoryBuffer bufferI2;
        static MemoryBuffer bufferR;

        static UInt32[] h_indexesA = new UInt32[INDEX_SIZE];
        static UInt32[] h_indexesB = new UInt32[INDEX_SIZE];

        static Job currentJob;
        static Job nextJob;
        static Stopwatch timer = new Stopwatch();

        public static ConcurrentQueue<Solution> graphSolutions = new ConcurrentQueue<Solution>();
        private volatile static int findersInFlight = 0;
        static volatile int trims = 0;
        static volatile int solutions = 0;
        private static volatile int trimRounds = 80;

        const string TestPrePow =
            "0001000000000000058d000000005c3f82a7000023bb9508c30beef7f54e502a293f224af3a1e654a831c23e58ba7c02b51aeaca1922f4bfeef3f4238c3077f8bcc6eb02e60c7d0e62dfc5aeeb4614ec4cdd24b1f94ad8464e5e56633d6b8f8a26d6696e11757a71e05cbb475f03867e01c70b913ce307370d0b145b791aecbe5abff9bcdea9f123be9f62636a171bc98897bc7b57884f59561d3ff4bc870e1e743da6505beedf9df6f9331f40f0b1b1741800000000000000000000000000000000000000000000000000000000000000000000000000000b160000000000000b16000000cb988c9ada000004ff";

        static void Main(string[] args)
        {
            try
            {
                if (args.Length > 0)
                    deviceID = int.Parse(args[0]);
                if (args.Length > 2)
                    platformID = int.Parse(args[2]);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, "Device ID parse error", ex);
            }

            try
            {
                if (args.Length > 1)
                {
                    port = int.Parse(args[1]);
                    Comms.ConnectToMaster(port);
                }
                else
                {
                    TEST = true;
                    CGraph.ShowCycles = true;
                    Logger.CopyToConsole = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, "Master connection error");
            }


            // Gets all available platforms and their corresponding devices, and prints them out in a table
            List<Platform> platforms = null;

            try
            {
                platforms = Platform.GetPlatforms().ToList();
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, "Failed to get OpenCL platform list");
                return;
            }

            if (TEST)
            {
                currentJob = nextJob = new Job()
                {
                    jobID = 2,
                    k0 = 0,
                    k1 = 0,
                    k2 = 0,
                    k3 = 0,
                    //k0 = 0x10ef16eadd6aa061L,
                    //k1 = 0x563f07e7a3c788b3L,
                    //k2 = 0xe8d7c8db1518f29aL,
                    //k3 = 0xc0ab7d1b4ca1adffL,
                    pre_pow = TestPrePow,
                    timestamp = DateTime.Now
                };
            }
            else
            {
                currentJob = nextJob = new Job()
                {
                    jobID = 0,
                    k0 = 0xf4956dc403730b01L,
                    k1 = 0xe6d45de39c2a5a3eL,
                    k2 = 0xcbf626a8afee35f6L,
                    k3 = 0x4307b94b1a0c9980L,
                    pre_pow = TestPrePow,
                    timestamp = DateTime.Now
                };

                if (!Comms.IsConnected())
                {
                    Console.WriteLine("Master connection failed, aborting");
                    Logger.Log(LogLevel.Error, "No master connection, exitting!");
                    Task.Delay(500).Wait();
                    return;
                }

                if (deviceID < 0)
                {
                    try
                    {
                        Environment.SetEnvironmentVariable("GPU_MAX_HEAP_SIZE", "100", EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("GPU_USE_SYNC_OBJECTS", "1", EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("GPU_MAX_ALLOC_PERCENT", "100",
                            EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("GPU_SINGLE_ALLOC_PERCENT", "100",
                            EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("GPU_64BIT_ATOMICS", "1", EnvironmentVariableTarget.User);
                        Environment.SetEnvironmentVariable("GPU_MAX_WORKGROUP_SIZE", "1024",
                            EnvironmentVariableTarget.User);
                        //Environment.SetEnvironmentVariable("AMD_OCL_BUILD_OPTIONS_APPEND", "-cl-std=CL2.0", EnvironmentVariableTarget.Machine);

                        GpuDevicesMessage gpum = new GpuDevicesMessage() {devices = new List<GpuDevice>()};
                        //foreach (Platform platform in platforms)
                        for (int p = 0; p < platforms.Count(); p++)
                        {
                            Platform platform = platforms[p];
                            var devices = platform.GetDevices(DeviceType.Gpu).ToList();
                            //foreach (Device device in platform.GetDevices(DeviceType.All))
                            for (int d = 0; d < devices.Count(); d++)
                            {
                                Device device = devices[d];
                                string name = device.Name;
                                string pName = platform.Name;
                                //Console.WriteLine(device.Name + " " + platform.Version.VersionString);
                                gpum.devices.Add(new GpuDevice()
                                {
                                    deviceID = d, platformID = p, platformName = pName, name = name,
                                    memory = device.GlobalMemorySize
                                });
                            }
                        }

                        Comms.gpuMsg = gpum;
                        Comms.SetEvent();
                        Task.Delay(1000).Wait();
                        Comms.Close();
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(LogLevel.Error, "Unable to enumerate OpenCL devices");
                        Task.Delay(500).Wait();
                        Comms.Close();
                        return;
                    }
                }
            }

            try
            {
                Device chosenDevice = null;
                try
                {
                    chosenDevice = platforms[platformID].GetDevices(DeviceType.Gpu).ToList()[deviceID];
                    Console.WriteLine($"Using OpenCL device: {chosenDevice.Name} ({chosenDevice.Vendor})");
                    Console.WriteLine();
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Error, $"Unable to select OpenCL device {deviceID} on platform {platformID} ");
                    Task.Delay(500).Wait();
                    Comms.Close();
                    return;
                }

                var assembly = Assembly.GetEntryAssembly();
                var resourceStream = assembly.GetManifestResourceStream("OclSolver.kernel.cl");
                using (StreamReader reader = new StreamReader(resourceStream))
                {
                    using (Context context = Context.CreateContext(chosenDevice))
                    {
                        /*
                         * Once the program has been created you can use clGetProgramInfo with CL_PROGRAM_BINARY_SIZES and then CL_PROGRAM_BINARIES, storing the resulting binary programs (one for each device of the context) into a buffer you supply. You can then save this binary data to disk for use in later runs.
                         * Not all devices might support binaries, so you will need to check the CL_PROGRAM_BINARY_SIZES result (it returns a zero size for that device if binaries are not supported).
                         */
                        using (OpenCl.DotNetCore.Programs.Program program =
                            context.CreateAndBuildProgramFromString(reader.ReadToEnd()))
                        {
                            using (CommandQueue commandQueue = CommandQueue.CreateCommandQueue(context, chosenDevice))
                            {
                                IntPtr clearPattern = IntPtr.Zero;
                                uint[] edgesCount;
                                int[] edgesLeft;
                                int trims = 0;
                                try
                                {
                                    clearPattern = Marshal.AllocHGlobal(4);
                                    Marshal.Copy(new byte[4] {0, 0, 0, 0}, 0, clearPattern, 4);

                                    try
                                    {
                                        bufferA1 = context.CreateBuffer<uint>(MemoryFlag.ReadWrite, BUFFER_SIZE_A1);
                                        bufferA2 = context.CreateBuffer<uint>(MemoryFlag.ReadWrite, BUFFER_SIZE_A2);
                                        bufferB = context.CreateBuffer<uint>(MemoryFlag.ReadWrite, BUFFER_SIZE_B);

                                        bufferI1 = context.CreateBuffer<uint>(MemoryFlag.ReadWrite, INDEX_SIZE);
                                        bufferI2 = context.CreateBuffer<uint>(MemoryFlag.ReadWrite, INDEX_SIZE);

                                        bufferR = context.CreateBuffer<uint>(MemoryFlag.ReadOnly, 42 * 2);
                                    }
                                    catch (Exception ex)
                                    {
                                        Task.Delay(500).Wait();
                                        Logger.Log(LogLevel.Error, "Unable to allocate buffers, out of memory?");
                                        Task.Delay(500).Wait();
                                        Comms.Close();
                                        return;
                                    }

                                    using (Kernel kernelSeedA = program.CreateKernel("FluffySeed2A"))
                                    using (Kernel kernelSeedB1 = program.CreateKernel("FluffySeed2B"))
                                    using (Kernel kernelSeedB2 = program.CreateKernel("FluffySeed2B"))
                                    using (Kernel kernelRound1 = program.CreateKernel("FluffyRound1"))
                                    using (Kernel kernelRoundO = program.CreateKernel("FluffyRoundNO1"))
                                    using (Kernel kernelRoundNA = program.CreateKernel("FluffyRoundNON"))
                                    using (Kernel kernelRoundNB = program.CreateKernel("FluffyRoundNON"))
                                    using (Kernel kernelTail = program.CreateKernel("FluffyTailO"))
                                    using (Kernel kernelRecovery = program.CreateKernel("FluffyRecovery"))
                                    {
                                        Stopwatch sw = new Stopwatch();
                                        //FluffySeed2A
                                        kernelSeedA.SetKernelArgumentGeneric(0, currentJob.k0); //v0i
                                        kernelSeedA.SetKernelArgumentGeneric(1, currentJob.k1); //v1i
                                        kernelSeedA.SetKernelArgumentGeneric(2, currentJob.k2); //v2i
                                        kernelSeedA.SetKernelArgumentGeneric(3, currentJob.k3); //v3i
                                        kernelSeedA.SetKernelArgument(4, bufferB); //* bufferA
                                        kernelSeedA.SetKernelArgument(5, bufferA1); //* bufferB
                                        kernelSeedA.SetKernelArgument(6, bufferI1); //* indexes

                                        //FluffyRecovery
                                        kernelRecovery.SetKernelArgumentGeneric(0, currentJob.k0); //v0i
                                        kernelRecovery.SetKernelArgumentGeneric(1, currentJob.k1); //v1i
                                        kernelRecovery.SetKernelArgumentGeneric(2, currentJob.k2); //v2i
                                        kernelRecovery.SetKernelArgumentGeneric(3, currentJob.k3); //v3i
                                        kernelRecovery.SetKernelArgument(4, bufferR); //* recovery
                                        kernelRecovery.SetKernelArgument(5, bufferI2); //* indexes
                                        
                                        //FluffySeed2B
                                        kernelSeedB1.SetKernelArgument(0, bufferA1); //* source
                                        kernelSeedB1.SetKernelArgument(1, bufferA1); //* destination1
                                        kernelSeedB1.SetKernelArgument(2, bufferA2); //* destination2
                                        kernelSeedB1.SetKernelArgument(3, bufferI1); //* sourceIndexes
                                        kernelSeedB1.SetKernelArgument(4, bufferI2); //* destinationIndexes
                                        kernelSeedB1.SetKernelArgumentGeneric(5, (uint) 32); //startBlock

                                        //FluffySeed2B
                                        kernelSeedB2.SetKernelArgument(0, bufferB); //* source
                                        kernelSeedB2.SetKernelArgument(1, bufferA1); //* destination1
                                        kernelSeedB2.SetKernelArgument(2, bufferA2); //* destination2
                                        kernelSeedB2.SetKernelArgument(3, bufferI1); //* sourceIndexes
                                        kernelSeedB2.SetKernelArgument(4, bufferI2); //* destinationIndexes
                                        kernelSeedB2.SetKernelArgumentGeneric(5, (uint) 0); //startBlock

                                        //FluffyRound1
                                        kernelRound1.SetKernelArgument(0, bufferA1); //* source1
                                        kernelRound1.SetKernelArgument(1, bufferA2); //* source2
                                        kernelRound1.SetKernelArgument(2, bufferB); //* destination
                                        kernelRound1.SetKernelArgument(3, bufferI2); //* sourceIndexes
                                        kernelRound1.SetKernelArgument(4, bufferI1); //* destinationIndexes
                                        kernelRound1.SetKernelArgumentGeneric(5, (uint) DUCK_SIZE_A * 1024); //bktInSize
                                        kernelRound1.SetKernelArgumentGeneric(6,
                                            (uint) DUCK_SIZE_B * 1024); //bktOutSize

                                        //FluffyRoundNO1
                                        kernelRoundO.SetKernelArgument(0, bufferB); //* source
                                        kernelRoundO.SetKernelArgument(1, bufferA1); //* destination
                                        kernelRoundO.SetKernelArgument(2, bufferI1); //* sourceIndexes
                                        kernelRoundO.SetKernelArgument(3, bufferI2); //* destinationIndexes

                                        //FluffyRoundNON
                                        kernelRoundNA.SetKernelArgument(0, bufferB); //* source
                                        kernelRoundNA.SetKernelArgument(1, bufferA1); //* destination
                                        kernelRoundNA.SetKernelArgument(2, bufferI1); //* sourceIndexes
                                        kernelRoundNA.SetKernelArgument(3, bufferI2); //* destinationIndexes

                                        //FluffyRoundNON
                                        kernelRoundNB.SetKernelArgument(0, bufferA1); //* source
                                        kernelRoundNB.SetKernelArgument(1, bufferB); //* destination
                                        kernelRoundNB.SetKernelArgument(2, bufferI2); //* sourceIndexes
                                        kernelRoundNB.SetKernelArgument(3, bufferI1); //* destinationIndexes

                                        //FluffyTailO
                                        kernelTail.SetKernelArgument(0, bufferB); //* source
                                        kernelTail.SetKernelArgument(1, bufferA1); //* destination
                                        kernelTail.SetKernelArgument(2, bufferI1); //* sourceIndexes
                                        kernelTail.SetKernelArgument(3, bufferI2); //* destinationIndexes



                                        int loopCnt = 0;
                                        //for (int i = 0; i < runs; i++)
                                        while (!Comms.IsTerminated)
                                        {
                                            try
                                            {
                                                if (!TEST && (string.IsNullOrEmpty(Comms.nextJob.pre_pow) ||
                                                              Comms.nextJob.pre_pow == TestPrePow))
                                                {
                                                    Logger.Log(LogLevel.Info, string.Format("Waiting for job...."));
                                                    Task.Delay(1000).Wait();
                                                    continue;
                                                }

                                                if (!TEST && ((currentJob.pre_pow != Comms.nextJob.pre_pow) ||
                                                              (currentJob.origin != Comms.nextJob.origin)))
                                                {
                                                    currentJob = Comms.nextJob;
                                                    currentJob.timestamp = DateTime.Now;
                                                }

                                                if (!TEST && (currentJob.timestamp.AddMinutes(30) < DateTime.Now) &&
                                                    Comms.lastIncoming.AddMinutes(30) < DateTime.Now)
                                                {
                                                    Logger.Log(LogLevel.Info, string.Format("Job too old..."));
                                                    Task.Delay(1000).Wait();
                                                    continue;
                                                }

                                                // test runs only once
                                                if (TEST && loopCnt++ > 100000)
                                                    Comms.IsTerminated = true;

                                                Logger.Log(LogLevel.Debug,
                                                    string.Format("GPU AMD{4}:Trimming #{4}: {0} {1} {2} {3}",
                                                        currentJob.k0, currentJob.k1, currentJob.k2, currentJob.k3,
                                                        currentJob.jobID, deviceID));

                                                //Stopwatch srw = new Stopwatch();
                                                //srw.Start();

                                                Solution s;
                                                while (graphSolutions.TryDequeue(out s))
                                                {
                                                    kernelRecovery.SetKernelArgumentGeneric(0, s.job.k0);
                                                    kernelRecovery.SetKernelArgumentGeneric(1, s.job.k1);
                                                    kernelRecovery.SetKernelArgumentGeneric(2, s.job.k2);
                                                    kernelRecovery.SetKernelArgumentGeneric(3, s.job.k3);
                                                    commandQueue.EnqueueWriteBufferEdges(bufferR, s.GetLongEdges());
                                                    commandQueue.EnqueueClearBuffer(bufferI2, 64 * 64 * 4,
                                                        clearPattern);
                                                    commandQueue.EnqueueNDRangeKernel(kernelRecovery, 1, 2048 * 256,
                                                        256, 0);
                                                    s.nonces = commandQueue.EnqueueReadBuffer<uint>(bufferI2, 42);
                                                    CommandQueuesNativeApi
                                                        .Finish(commandQueue.Handle);
                                                    s.nonces = s.nonces.OrderBy(n => n).ToArray();
                                                    Comms.graphSolutionsOut.Enqueue(s);
                                                    Comms.SetEvent();
                                                }

                                                //srw.Stop();
                                                //Console.WriteLine("RECOVERY " + srw.ElapsedMilliseconds);

                                                currentJob = currentJob.Next();

                                                kernelSeedA.SetKernelArgumentGeneric(0, currentJob.k0);
                                                kernelSeedA.SetKernelArgumentGeneric(1, currentJob.k1);
                                                kernelSeedA.SetKernelArgumentGeneric(2, currentJob.k2);
                                                kernelSeedA.SetKernelArgumentGeneric(3, currentJob.k3);

                                                sw.Restart();

                                                commandQueue.EnqueueClearBuffer(bufferI2, 64 * 64 * 4, clearPattern);
                                                commandQueue.EnqueueClearBuffer(bufferI1, 64 * 64 * 4, clearPattern);
                                                commandQueue.EnqueueNDRangeKernel(kernelSeedA, 1, 2048 * 128, 128, 0);
                                                commandQueue.EnqueueNDRangeKernel(kernelSeedB1, 1, 1024 * 128, 128, 0);
                                                commandQueue.EnqueueNDRangeKernel(kernelSeedB2, 1, 1024 * 128, 128, 0);
                                                commandQueue.EnqueueClearBuffer(bufferI1, 64 * 64 * 4, clearPattern);
                                                commandQueue.EnqueueNDRangeKernel(kernelRound1, 1, 4096 * 1024, 1024,
                                                    0);

                                                commandQueue.EnqueueClearBuffer(bufferI2, 64 * 64 * 4, clearPattern);
                                                commandQueue.EnqueueNDRangeKernel(kernelRoundO, 1, 4096 * 1024, 1024,
                                                    0);
                                                commandQueue.EnqueueClearBuffer(bufferI1, 64 * 64 * 4, clearPattern);
                                                commandQueue.EnqueueNDRangeKernel(kernelRoundNB, 1, 4096 * 1024, 1024,
                                                    0);

                                                for (int r = 0; r < trimRounds; r++)
                                                {
                                                    commandQueue.EnqueueClearBuffer(bufferI2, 64 * 64 * 4,
                                                        clearPattern);
                                                    commandQueue.EnqueueNDRangeKernel(kernelRoundNA, 1, 4096 * 1024,
                                                        1024, 0);
                                                    commandQueue.EnqueueClearBuffer(bufferI1, 64 * 64 * 4,
                                                        clearPattern);
                                                    commandQueue.EnqueueNDRangeKernel(kernelRoundNB, 1, 4096 * 1024,
                                                        1024, 0);
                                                }

                                                commandQueue.EnqueueClearBuffer(bufferI2, 64 * 64 * 4, clearPattern);
                                                commandQueue.EnqueueNDRangeKernel(kernelTail, 1, 4096 * 1024, 1024, 0);

                                                edgesCount = commandQueue.EnqueueReadBuffer<uint>(bufferI2, 1);
                                                edgesCount[0] = edgesCount[0] > 1000000 ? 1000000 : edgesCount[0];
                                                edgesLeft = commandQueue.EnqueueReadBuffer(bufferA1,
                                                    (int) edgesCount[0] * 2);

                                                CommandQueuesNativeApi.Flush(
                                                    commandQueue.Handle);
                                                CommandQueuesNativeApi.Finish(
                                                    commandQueue.Handle);

                                                sw.Stop();

                                                currentJob.trimTime = sw.ElapsedMilliseconds;
                                                currentJob.solvedAt = DateTime.Now;


                                                Logger.Log(LogLevel.Info,
                                                    string.Format("GPU AMD{2}:    Trimmed in {0}ms to {1} edges",
                                                        sw.ElapsedMilliseconds, edgesCount[0], deviceID));

                                                CGraph cg = new CGraph();
                                                cg.SetEdges(edgesLeft, (int) edgesCount[0]);
                                                cg.SetHeader(currentJob);

                                                Task.Factory.StartNew(() =>
                                                {
                                                    if (edgesCount[0] < 200000)
                                                    {
                                                        try
                                                        {
                                                            if (findersInFlight++ < 3)
                                                            {
                                                                Stopwatch cycleTime = new Stopwatch();
                                                                cycleTime.Start();
                                                                cg.FindSolutions(graphSolutions);
                                                                cycleTime.Stop();
                                                                AdjustTrims(cycleTime.ElapsedMilliseconds);
                                                                if (TEST)
                                                                {
                                                                    Logger.Log(LogLevel.Info,
                                                                        string.Format(
                                                                            "Finder completed in {0}ms on {1} edges with {2} solution(s) and {3} dupes",
                                                                            sw.ElapsedMilliseconds, edgesCount[0],
                                                                            graphSolutions.Count, cg.dupes));

                                                                    if (++trims % 50 == 0)
                                                                    {
                                                                        Console.ForegroundColor = ConsoleColor.Green;
                                                                        Console.WriteLine(
                                                                            "SOLS: {0}/{1} - RATE: {2:F1}", solutions,
                                                                            trims, (float) trims / solutions);
                                                                        Console.ResetColor();
                                                                    }
                                                                }

                                                                if (graphSolutions.Count > 0)
                                                                {
                                                                    solutions++;
                                                                }
                                                            }
                                                            else
                                                                Logger.Log(LogLevel.Warning, "CPU overloaded!");
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            Logger.Log(LogLevel.Error, "Cycle finder crashed", ex);
                                                        }
                                                        finally
                                                        {
                                                            findersInFlight--;
                                                        }
                                                    }
                                                });
                                            }
                                            catch (Exception ex)
                                            {
                                                Logger.Log(LogLevel.Error,
                                                    "Critical error in main ocl loop: " + ex.Message);
                                                Task.Delay(5000).Wait();
                                            }
                                        }

                                        //uint[] resultArray = commandQueue.EnqueueReadBuffer<uint>(bufferI1, 64 * 64);
                                        //uint[] resultArray2 = commandQueue.EnqueueReadBuffer<uint>(bufferI2, 64 * 64);
                                        //Console.WriteLine("SeedA: " + resultArray.Sum(e => e) + " in " + sw.ElapsedMilliseconds / runs);
                                        //Console.WriteLine("SeedB: " + resultArray2.Sum(e => e) + " in " + sw.ElapsedMilliseconds / runs);
                                        //Task.Delay(1000).Wait();
                                        //Console.WriteLine("");
                                    }
                                }
                                finally
                                {
                                    // clear pattern
                                    if (clearPattern != IntPtr.Zero)
                                        Marshal.FreeHGlobal(clearPattern);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, "Critical error in OCL Init " + ex.Message);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(ex.Message);
                Console.ResetColor();
                Task.Delay(500).Wait();
            }
            finally
            {
                Task.Delay(500).Wait();

                try
                {
                    Comms.Close();
                    bufferA1.Dispose();
                    bufferA2.Dispose();
                    bufferB.Dispose();
                    bufferI1.Dispose();
                    bufferI2.Dispose();
                    bufferR.Dispose();

                    if (CommandQueue.resultValuePointer != IntPtr.Zero)
                        Marshal.FreeHGlobal(CommandQueue.resultValuePointer);
                }
                catch
                {
                }
            }

            //Console.ReadKey();
        }

        private static void AdjustTrims(long elapsedMilliseconds)
        {
            int target = Comms.cycleFinderTargetOverride > 0
                ? Comms.cycleFinderTargetOverride
                : 20 * Environment.ProcessorCount;
            if (elapsedMilliseconds > target)
                trimRounds += 10;
            else
                trimRounds -= 10;

            trimRounds = Math.Max(80, trimRounds);
            trimRounds = Math.Min(300, trimRounds);
        }
    }
}