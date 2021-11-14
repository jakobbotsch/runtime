// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Internal.IL;
using Internal.IL.Stubs;
using Internal.TypeSystem;

namespace ILCompiler.ReadyToRun.IL.Stubs
{
    public class TrivialILStub : ILStubMethod
    {
        public TrivialILStub(TypeSystemContext context)
        {
            Context = context;
        }

        public override string DiagnosticName => "TrivialILStub";
        public override string Name => "TrivialILStub";

        public override TypeDesc OwningType => Context.SystemModule.GetKnownType("System.StubHelpers", "StubHelpers");

        public override MethodSignature Signature => new MethodSignature(MethodSignatureFlags.None, 0, Context.GetWellKnownType(WellKnownType.Void), Array.Empty<TypeDesc>());

        public override TypeSystemContext Context { get; }

        protected override int ClassCode => 123456789;

        public override MethodIL EmitIL()
        {
            ILEmitter emit = new ILEmitter();
            ILCodeStream codeStream = emit.NewCodeStream();
            codeStream.Emit(ILOpcode.ldc_i4_0);
            codeStream.Emit(ILOpcode.conv_u);
            codeStream.Emit(ILOpcode.ldind_i1);
            codeStream.Emit(ILOpcode.pop);
            codeStream.Emit(ILOpcode.ret);
            return emit.Link(this);
        }

        protected override int CompareToImpl(MethodDesc other, TypeSystemComparer comparer)
        {
            if (other is not TrivialILStub triv)
                return 1;

            return 0;
        }
    }
}
