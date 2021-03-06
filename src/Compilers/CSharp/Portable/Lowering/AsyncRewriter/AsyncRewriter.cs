﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CodeGen;
using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class AsyncRewriter : StateMachineRewriter
    {
        private readonly AsyncMethodBuilderMemberCollection asyncMethodBuilderMemberCollection;
        private readonly bool constructedSuccessfully;
        private readonly int methodOrdinal;

        private FieldSymbol builderField;

        private AsyncRewriter(
            BoundStatement body,
            SourceMethodSymbol method,
            int methodOrdinal,
            AsyncStateMachine stateMachineType,
            VariableSlotAllocator slotAllocatorOpt,
            TypeCompilationState compilationState,
            DiagnosticBag diagnostics)
            : base(body, method, stateMachineType, slotAllocatorOpt, compilationState, diagnostics)
        {
            try
            {
                constructedSuccessfully = AsyncMethodBuilderMemberCollection.TryCreate(F, method, this.stateMachineType.TypeMap, out this.asyncMethodBuilderMemberCollection);
            }
            catch (SyntheticBoundNodeFactory.MissingPredefinedMember ex)
            {
                diagnostics.Add(ex.Diagnostic);
                constructedSuccessfully = false;
            }

            this.methodOrdinal = methodOrdinal;
        }

        /// <summary>
        /// Rewrite an async method into a state machine type.
        /// </summary>
        internal static BoundStatement Rewrite(
            BoundStatement body,
            MethodSymbol method,
            int methodOrdinal,
            VariableSlotAllocator slotAllocatorOpt,
            TypeCompilationState compilationState,
            DiagnosticBag diagnostics,
            out AsyncStateMachine stateMachineType)
        {
            if (!method.IsAsync)
            {
                stateMachineType = null;
                return body;
            }

            // The CLR doesn't support adding fields to structs, so in order to enable EnC in an async method we need to generate a class.
            var typeKind = compilationState.Compilation.Options.EnableEditAndContinue ? TypeKind.Class : TypeKind.Struct;

            var bodyWithAwaitLifted = AwaitExpressionSpiller.Rewrite(body, method, compilationState, diagnostics);

            stateMachineType = new AsyncStateMachine(slotAllocatorOpt, compilationState, method, methodOrdinal, typeKind);
            compilationState.ModuleBuilderOpt.CompilationState.SetStateMachineType(method, stateMachineType);
            var rewriter = new AsyncRewriter(bodyWithAwaitLifted, (SourceMethodSymbol)method, methodOrdinal, stateMachineType, slotAllocatorOpt, compilationState, diagnostics);
            if (!rewriter.constructedSuccessfully)
            {
                return body;
            }

            return rewriter.Rewrite();
        }

        protected override bool PreserveInitialParameterValues
        {
            get { return false; }
        }

        protected override void GenerateControlFields()
        {
            // the fields are initialized from async method, so they need to be public:

            this.stateField = F.StateMachineField(F.SpecialType(SpecialType.System_Int32), GeneratedNames.MakeStateMachineStateFieldName(), isPublic: true);
            this.builderField = F.StateMachineField(asyncMethodBuilderMemberCollection.BuilderType, GeneratedNames.AsyncBuilderFieldName(), isPublic: true);
        }

        protected override void GenerateMethodImplementations()
        {
            var IAsyncStateMachine_MoveNext = F.WellKnownMethod(WellKnownMember.System_Runtime_CompilerServices_IAsyncStateMachine_MoveNext);
            var IAsyncStateMachine_SetStateMachine = F.WellKnownMethod(WellKnownMember.System_Runtime_CompilerServices_IAsyncStateMachine_SetStateMachine);

            // Add IAsyncStateMachine.MoveNext()

            var moveNextMethod = OpenMethodImplementation(
                IAsyncStateMachine_MoveNext,
                WellKnownMemberNames.MoveNextMethodName, 
                debuggerHidden: IsDebuggerHidden(this.method), 
                generateDebugInfo: true,
                hasMethodBodyDependency: true);

            GenerateMoveNext(moveNextMethod);

            // Add IAsyncStateMachine.SetStateMachine()
            
            OpenMethodImplementation(
                IAsyncStateMachine_SetStateMachine,
                "SetStateMachine", 
                debuggerHidden: true, 
                generateDebugInfo: false, 
                hasMethodBodyDependency: false);

            // SetStateMachine is used to initialize the underlying AsyncMethodBuilder's reference to the boxed copy of the state machine.
            // If the state machine is a class there is no copy made and thus the initialization is not necessary. 
            // In fact it is an error to reinitialize the builder since it already is initialized.
            if (F.CurrentType.TypeKind == TypeKind.Class)
            {
                F.CloseMethod(F.Return());
            }
            else
            {
                F.CloseMethod(
                    // this.builderField.SetStateMachine(sm)
                    F.Block(
                        F.ExpressionStatement(
                            F.Call(
                                F.Field(F.This(), builderField),
                                asyncMethodBuilderMemberCollection.SetStateMachine,
                                new BoundExpression[] { F.Parameter(F.CurrentMethod.Parameters[0]) })),
                        F.Return()));
            }

            // Constructor
            if (stateMachineType.TypeKind == TypeKind.Class)
            {
                F.CurrentMethod = stateMachineType.Constructor;
                F.CloseMethod(F.Block(ImmutableArray.Create(F.BaseInitialization(), F.Return())));
            }
        }

        protected override void InitializeStateMachine(ArrayBuilder<BoundStatement> bodyBuilder, NamedTypeSymbol frameType, LocalSymbol stateMachineLocal)
        {
            if (frameType.TypeKind == TypeKind.Class)
            {
                // local = new {state machine type}();
                bodyBuilder.Add(
                    F.Assignment(
                        F.Local(stateMachineLocal),
                        F.New(frameType.InstanceConstructors[0])));
            }
        }

        protected override BoundStatement GenerateStateMachineCreation(LocalSymbol stateMachineVariable, NamedTypeSymbol frameType)
        {
            try
            {
                var bodyBuilder = ArrayBuilder<BoundStatement>.GetInstance();

                // If the async method's result type is a type parameter of the method, then the AsyncTaskMethodBuilder<T>
                // needs to use the method's type parameters inside the rewritten method body. All other methods generated
                // during async rewriting are members of the synthesized state machine struct, and use the type parameters
                // structs type parameters.
                AsyncMethodBuilderMemberCollection methodScopeAsyncMethodBuilderMemberCollection;
                if (!AsyncMethodBuilderMemberCollection.TryCreate(F, method, null, out methodScopeAsyncMethodBuilderMemberCollection))
                {
                    return new BoundBadStatement(F.Syntax, ImmutableArray<BoundNode>.Empty, hasErrors: true);
                }

                var builderVariable = F.SynthesizedLocal(methodScopeAsyncMethodBuilderMemberCollection.BuilderType, null);

                // local.$builder = System.Runtime.CompilerServices.AsyncTaskMethodBuilder<typeArgs>.Create();
                bodyBuilder.Add(
                    F.Assignment(
                        F.Field(F.Local(stateMachineVariable), builderField.AsMember(frameType)),
                        F.StaticCall(methodScopeAsyncMethodBuilderMemberCollection.BuilderType, "Create", ImmutableArray<TypeSymbol>.Empty)));

                // local.$stateField = NotStartedStateMachine
                bodyBuilder.Add(
                    F.Assignment(
                        F.Field(F.Local(stateMachineVariable), stateField.AsMember(frameType)),
                        F.Literal(StateMachineStates.NotStartedStateMachine)));

                bodyBuilder.Add(
                    F.Assignment(
                        F.Local(builderVariable),
                        F.Field(F.Local(stateMachineVariable), builderField.AsMember(frameType))));

                // local.$builder.Start(ref local) -- binding to the method AsyncTaskMethodBuilder<typeArgs>.Start()
                bodyBuilder.Add(
                    F.ExpressionStatement(
                        F.Call(
                            F.Local(builderVariable),
                            methodScopeAsyncMethodBuilderMemberCollection.Start.Construct(frameType),
                            ImmutableArray.Create<BoundExpression>(F.Local(stateMachineVariable)))));

                bodyBuilder.Add(method.IsVoidReturningAsync()
                    ? F.Return()
                    : F.Return(F.Property(F.Field(F.Local(stateMachineVariable), builderField.AsMember(frameType)), "Task")));

                return F.Block(
                    ImmutableArray.Create(builderVariable),
                    bodyBuilder.ToImmutableAndFree());
            }
            catch (SyntheticBoundNodeFactory.MissingPredefinedMember ex)
            {
                F.Diagnostics.Add(ex.Diagnostic);
                return new BoundBadStatement(F.Syntax, ImmutableArray<BoundNode>.Empty, hasErrors: true);
            }
        }

        private void GenerateMoveNext(SynthesizedImplementationMethod moveNextMethod)
        {
            var rewriter = new AsyncMethodToStateMachineRewriter(
                method: method,
                methodOrdinal: methodOrdinal,
                asyncMethodBuilderMemberCollection: asyncMethodBuilderMemberCollection,
                F: F,
                state: stateField,
                builder: builderField,
                hoistedVariables: hoistedVariables,
                nonReusableLocalProxies: nonReusableLocalProxies,
                synthesizedLocalOrdinals: synthesizedLocalOrdinals,
                slotAllocatorOpt: slotAllocatorOpt,
                nextFreeHoistedLocalSlot: nextFreeHoistedLocalSlot,
                diagnostics: diagnostics);

            rewriter.GenerateMoveNext(body, moveNextMethod);
        }
    }
}
