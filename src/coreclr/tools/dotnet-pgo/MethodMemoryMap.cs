// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Internal.TypeSystem;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Microsoft.Diagnostics.Tools.Pgo
{
    // A map that can be used to resolve memory addresses back to the MethodDesc.
    internal class MethodMemoryMap
    {
        private readonly InstructionPointerRange[] _ranges;
        private readonly MethodDesc[] _mds;

        public MethodMemoryMap(
            TraceProcess p,
            TraceTypeSystemContext tsc,
            TraceRuntimeDescToTypeSystemDesc idParser,
            int clrInstanceID)
        {
            // Capture the addresses of jitted code
            List<ValueTuple<InstructionPointerRange, MethodDesc>> codeLocations = new List<(InstructionPointerRange, MethodDesc)>();
            foreach (var e in p.EventsInProcess.ByEventType<MethodLoadUnloadTraceData>())
            {
                if (e.ClrInstanceID != clrInstanceID)
                {
                    continue;
                }

                //if (e.OptimizationTier != OptimizationTier.QuickJitted)
                //    continue;

                MethodDesc method = null;
                try
                {
                    method = idParser.ResolveMethodID(e.MethodID);
                }
                catch
                {
                }

                if (method != null)
                {
                    codeLocations.Add((new InstructionPointerRange(e.MethodStartAddress, e.MethodSize), method));
                }
            }

            foreach (var e in p.EventsInProcess.ByEventType<MethodLoadUnloadVerboseTraceData>())
            {
                if (e.ClrInstanceID != clrInstanceID)
                {
                    continue;
                }

                //if (e.OptimizationTier != OptimizationTier.QuickJitted)
                //    continue;

                MethodDesc method = null;
                try
                {
                    method = idParser.ResolveMethodID(e.MethodID);
                }
                catch
                {
                }

                if (method != null)
                {
                    codeLocations.Add((new InstructionPointerRange(e.MethodStartAddress, e.MethodSize), method));
                }
            }

            var sigProvider = new R2RSignatureTypeProvider(tsc);
            foreach (var module in p.LoadedModules)
            {
                if (module.FilePath == "")
                    continue;

                if (!File.Exists(module.FilePath))
                    continue;

                try
                {
                    byte[] image = File.ReadAllBytes(module.FilePath);
                    using (FileStream fstream = new FileStream(module.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var r2rCheckPEReader = new System.Reflection.PortableExecutable.PEReader(fstream, System.Reflection.PortableExecutable.PEStreamOptions.LeaveOpen);

                        if (!ILCompiler.Reflection.ReadyToRun.ReadyToRunReader.IsReadyToRunImage(r2rCheckPEReader))
                            continue;
                    }

                    var reader = new ILCompiler.Reflection.ReadyToRun.ReadyToRunReader(tsc, module.FilePath);
                    foreach (var methodEntry in reader.GetCustomMethodToRuntimeFunctionMapping<TypeDesc, MethodDesc, R2RSigProviderContext>(sigProvider))
                    {
                        foreach (var runtimeFunction in methodEntry.Value.RuntimeFunctions)
                        {
                            codeLocations.Add((new InstructionPointerRange(module.ImageBase + (ulong)runtimeFunction.StartAddress, runtimeFunction.Size), methodEntry.Key));
                        }
                    }
                }
                catch { }
            }

            _ranges = new InstructionPointerRange[codeLocations.Count];
            _mds = new MethodDesc[codeLocations.Count];
            for (int i = 0; i < codeLocations.Count; i++)
            {
                _ranges[i] = codeLocations[i].Item1;
                _mds[i] = codeLocations[i].Item2;
            }

            Array.Sort(_ranges, _mds);
        }

        public MethodDesc ResolveIP(ulong ip)
        {
            int index = Array.BinarySearch(_ranges, new InstructionPointerRange(ip, 1));

            if (index >= 0)
            {
                return _mds[index];
            }
            else
            {
                index = ~index;
                if (index >= _ranges.Length)
                    return null;

                if (_ranges[index].StartAddress < ip)
                {
                    if (_ranges[index].EndAddress > ip)
                    {
                        return _mds[index];
                    }
                }

                if (index == 0)
                    return null;

                index--;

                if (_ranges[index].StartAddress < ip)
                {
                    if (_ranges[index].EndAddress > ip)
                    {
                        return _mds[index];
                    }
                }

                return null;
            }
        }

        private struct InstructionPointerRange : IComparable<InstructionPointerRange>
        {
            public InstructionPointerRange(ulong startAddress, int size)
            {
                StartAddress = startAddress;
                EndAddress = startAddress + (ulong)size;
            }

            public ulong StartAddress;
            public ulong EndAddress;

            public int CompareTo(InstructionPointerRange other)
            {
                if (StartAddress < other.StartAddress)
                {
                    return -1;
                }
                if (StartAddress > other.StartAddress)
                {
                    return 1;
                }
                return (int)((long)EndAddress - (long)other.EndAddress);
            }
        }
    }
}
