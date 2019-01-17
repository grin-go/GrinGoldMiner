using System;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenCl.DotNetCore.CommandQueues;
using OpenCl.DotNetCore.Contexts;
using OpenCl.DotNetCore.Devices;
using OpenCl.DotNetCore.Kernels;
using OpenCl.DotNetCore.Memory;
using OpenCl.DotNetCore.Platforms;

namespace OpenClTest
{
    class Program
    {
        static string vecSum = @"
         
          __kernel void myKernel(__global int* result) {
            for(int i=0;i<2000000;i++)
                {
                    result[i]=i*i;
                }
         } ";

        static void Main(string[] args)
        {
            int n = 100;
            float[] v1 = new float[n], v2 = new float[n], v3 = new float[n];
            //Инициализация и присвоение векторов, которые мы будем складывать.
            for (int i = 0; i < n; i++)
            {
                v1[i] = i;
                v2[i] = i * 2;
            }

            var platforms = Platform.GetPlatforms().ToList();
            var chosenDevice = platforms[0].GetDevices(DeviceType.Gpu).ToList()[0];
            var assembly = Assembly.GetEntryAssembly();
            var resourceStream = ToStream(vecSum);
            using (StreamReader reader = new StreamReader(resourceStream))
            {
                using (Context context = Context.CreateContext(chosenDevice))
                {
                    using (OpenCl.DotNetCore.Programs.Program program =
                        context.CreateAndBuildProgramFromString(reader.ReadToEnd()))
                    {
                        using (CommandQueue commandQueue = CommandQueue.CreateCommandQueue(context, chosenDevice))
                        {
                            using (Kernel kernelSeedA = program.CreateKernel("myKernel"))
                            {
                                MemoryBuffer result=context.CreateBuffer<uint>(MemoryFlag.KernelReadAndWrite, 8);
                         
                                kernelSeedA.SetKernelArgument(0, result); //v0i
                                commandQueue.EnqueueNDRangeKernel(kernelSeedA, 1, 64, 128,
                                    0);
                                int[] edgesLeft = commandQueue.EnqueueReadBuffer(result,1);
                                foreach (var i in edgesLeft)
                                {
                                    Console.WriteLine("Hello World! "+edgesLeft[i]);
                                }
                              
                            
                            }
                        }

                        
                    }
                }
            }

            Console.WriteLine("Hello World!");
        }

        public static Stream ToStream(string str)
        {
            MemoryStream stream = new MemoryStream();
            StreamWriter writer = new StreamWriter(stream);
            writer.Write(str);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }
    }
}