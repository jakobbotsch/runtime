// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Internal.Pgo;

namespace Microsoft.Diagnostics.Tools.Pgo.SamplePGO
{
    public class InstrumentedPgoProfile
    {
        public InstrumentedPgoProfile(uint codeHash, uint methodHash, int iLSize, string methodName, string signature, List<PgoSchemaElem> schema)
        {
            CodeHash = codeHash;
            MethodHash = methodHash;
            ILSize = iLSize;
            MethodName = methodName;
            Signature = signature;
            Schema = schema;
        }

        public uint CodeHash { get; }
        public uint MethodHash { get; }
        public int ILSize { get; }
        public string MethodName { get; }
        public string Signature { get; }
        public List<PgoSchemaElem> Schema { get; }

        private static readonly Regex s_startRegex = new Regex("^\\*\\*\\* START PGO Data, max index = ([0-9]+) \\*\\*\\*$", RegexOptions.Compiled);
        private static readonly Regex s_methodDescrRegex = new Regex("^@@@ codehash 0x([0-9A-Za-z]+) methodhash 0x([0-9A-Za-z]+) ilSize 0x([0-9A-Za-z]+) records 0x([0-9A-Za-z]+)$", RegexOptions.Compiled);
        private static readonly Regex s_methodNameRegex = new Regex("^MethodName: (.*)$", RegexOptions.Compiled);
        private static readonly Regex s_methodSignatureRegex = new Regex("^Signature: (.*)$", RegexOptions.Compiled);
        private static readonly Regex s_schemaHeader = new Regex("^Schema InstrumentationKind ([0-9]+) ILOffset ([0-9]+) Count ([0-9]+) Other ([0-9]+)$", RegexOptions.Compiled);
        private static readonly Regex s_schemaFourByte = new Regex("^[0-9]+$", RegexOptions.Compiled);
        private static readonly Regex s_schemaEightByte = new Regex("^([0-9]+) ([0-9]+)$", RegexOptions.Compiled);
        private static readonly Regex s_schemaTypeHandleValue = new Regex("^TypeHandle: (.*)$", RegexOptions.Compiled);
        private static readonly Regex s_endRegex = new Regex("^\\*\\*\\* END PGO Data \\*\\*\\*$", RegexOptions.Compiled);

        public static List<InstrumentedPgoProfile> Parse(TextReader tr)
        {
            Match Next(Regex r)
            {
                string line = tr.ReadLine();
                Match m = r.Match(line);
                if (!m.Success)
                    throw new InvalidDataException("Cannot parse PGO profile");

                return m;
            }

            Match header = Next(s_startRegex);
            int num = int.Parse(header.Groups[1].Value);
            List<InstrumentedPgoProfile> parsed = new List<InstrumentedPgoProfile>(num);
            for (int i = 0; i < num; i++)
            {
                Match methodDesc = Next(s_methodDescrRegex);
                uint codeHash = uint.Parse(methodDesc.Groups[1].Value, NumberStyles.HexNumber);
                uint methodHash = uint.Parse(methodDesc.Groups[2].Value, NumberStyles.HexNumber);
                uint ilSize = uint.Parse(methodDesc.Groups[3].Value, NumberStyles.HexNumber);
                uint numRecords = uint.Parse(methodDesc.Groups[4].Value, NumberStyles.HexNumber);
                Match methodName = Next(s_methodNameRegex);
                Match signature = Next(s_methodSignatureRegex);
                List<PgoSchemaElem> schema = new List<PgoSchemaElem>(checked((int)numRecords));
                for (uint j = 0; j < numRecords; j++)
                {
                    Match schemaHeader = Next(s_schemaHeader);
                    PgoInstrumentationKind instrKind = (PgoInstrumentationKind)int.Parse(schemaHeader.Groups[1].Value);
                    uint ilOffset = uint.Parse(schemaHeader.Groups[2].Value);
                    uint count = uint.Parse(schemaHeader.Groups[3].Value);
                    uint other = uint.Parse(schemaHeader.Groups[4].Value);
                    long dataValue = 0;
                    Array dataObj = null;
                    switch (instrKind & PgoInstrumentationKind.MarshalMask)
                    {
                        case PgoInstrumentationKind.FourByte:
                        case PgoInstrumentationKind.EightByte:
                            Span<long> longs = count < 128 ? stackalloc long[checked((int)count)] : new long[count];
                            for (int k = 0; k < longs.Length; k++)
                            {
                                if ((instrKind & PgoInstrumentationKind.MarshalMask) == PgoInstrumentationKind.FourByte)
                                {
                                    Match valueMatch = Next(s_schemaFourByte);
                                    longs[k] = long.Parse(valueMatch.Value);
                                }
                                else
                                {
                                    Match valueMatch = Next(s_schemaEightByte);
                                    uint lo = uint.Parse(valueMatch.Groups[1].Value);
                                    uint hi = uint.Parse(valueMatch.Groups[2].Value);
                                    longs[k] = ((long)hi << 32) | lo;
                                }
                            }

                            if (count == 1)
                                dataValue = longs[0];
                            else
                                dataObj = longs.ToArray();
                            break;
                        case PgoInstrumentationKind.TypeHandle:
                            string[] ths = new string[count];
                            for (int k = 0; k < ths.Length; k++)
                            {
                                Match thMatch = Next(s_schemaTypeHandleValue);
                                ths[k] = thMatch.Groups[1].Value;
                            }
                            dataObj = ths;
                            break;
                        default:
                            Trace.Fail("Invalid kind " + instrKind);
                            break;
                    }

                    schema.Add(new PgoSchemaElem
                    {
                        InstrumentationKind = instrKind,
                        ILOffset = (int)ilOffset,
                        Count = (int)count,
                        Other = (int)other,
                        DataLong = dataValue,
                        DataObject = dataObj,
                    });
                }

                parsed.Add(new InstrumentedPgoProfile(codeHash, methodHash, checked((int)ilSize), methodName.Groups[1].Value, signature.Groups[1].Value, schema));
            }

            Next(s_endRegex);
            return parsed;
        }
    }
}
