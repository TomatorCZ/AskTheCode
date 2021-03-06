﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AskTheCode.ControlFlowGraphs;
using AskTheCode.ControlFlowGraphs.Heap;
using AskTheCode.PathExploration.Heap;
using AskTheCode.SmtLibStandard;
using AskTheCode.SmtLibStandard.Handles;
using CodeContractsRevival.Runtime;

namespace AskTheCode.PathExploration
{
    internal class PathConditionHandler : PathVariableVersionHandler
    {
        private readonly ISolver smtSolver;

        public PathConditionHandler(
            SmtContextHandler smtContextHandler,
            ISolver smtSolver,
            Path path,
            StartingNodeInfo startingNode,
            ISymbolicHeap heap)
            : base(path, startingNode, smtContextHandler)
        {
            Contract.Requires(smtSolver != null);
            Contract.Requires(heap != null);

            this.smtSolver = smtSolver;
            this.Heap = heap;

            this.smtSolver.Push();
            this.Heap.PushState();
        }

        internal ISymbolicHeap Heap { get; }

        protected override void OnAfterPathRetracted(int popCount)
        {
            // It is done as batch for performance reasons
            if (popCount > 0)
            {
                this.smtSolver.Pop(popCount);
                this.Heap.PopState(popCount);
            }
        }

        protected override void OnBeforePathStepExtended(FlowEdge edge)
        {
            this.smtSolver.Push();
            this.Heap.PushState();

            if (edge is OuterFlowEdge outerEdge)
            {
                if (outerEdge.Kind == OuterFlowEdgeKind.MethodCall
                    && outerEdge.To is EnterFlowNode enterNode)
                {
                    var callNode = (CallFlowNode)outerEdge.From;
                    if (callNode.IsObjectCreation)
                    {
                        // This is needed in the case when the exploration itself started from a constructor (or from a
                        // method called by it). We need to let the heap know that this object is not a part of the input
                        // heap.
                        var newVar = enterNode.Parameters[0];
                        var versionedVar = new VersionedVariable(newVar, this.GetVariableVersion(newVar));
                        this.Heap.AllocateNew(versionedVar);
                    }
                    else if (callNode.IsInstanceCall)
                    {
                        var thisVar = enterNode.Parameters[0];
                        var versionedThisVar = new VersionedVariable(thisVar, this.GetVariableVersion(thisVar));
                        this.Heap.AssertEquality(false, versionedThisVar, VersionedVariable.Null);
                    }
                }
            }
        }

        protected override void OnConditionAsserted(BoolHandle condition)
        {
            this.smtSolver.AddAssertion(this.NameProvider, condition);
        }

        protected override void OnVariableAssigned(
            FlowVariable variable,
            int lastVersion,
            Expression value)
        {
            if (variable.IsReference)
            {
                var leftRef = new VersionedVariable(variable, lastVersion);
                var rightRef = this.GetVersioned((FlowVariable)value);

                this.Heap.AssignReference(leftRef, rightRef);
            }
            else if (References.IsReferenceComparison(value, out bool areEqual, out var left, out var right))
            {
                var varLeft = this.GetVersioned(left);
                var varRight = this.GetVersioned(right);
                value = this.Heap.GetEqualityExpression(areEqual, varLeft, varRight);
            }

            if (!variable.IsReference)
            {
                this.AssertEquals(variable, lastVersion, value);
            }
        }

        protected override void OnReferenceEqualityAsserted(
            bool areEqual,
            VersionedVariable left,
            VersionedVariable right)
        {
            this.Heap.AssertEquality(areEqual, left, right);
        }

        protected override void OnFieldReadAsserted(
            VersionedVariable result,
            VersionedVariable reference,
            IFieldDefinition field)
        {
            this.Heap.ReadField(result, reference, field);
        }

        protected override void OnFieldWriteAsserted(
            VersionedVariable reference,
            IFieldDefinition field,
            Expression value)
        {
            this.Heap.WriteField(reference, field, value);
        }

        private void AssertEquals(FlowVariable variable, int version, Expression value)
        {
            var symbolName = this.SmtContextHandler.GetVariableVersionSymbol(variable, version);
            var symbolWrapper = ExpressionFactory.NamedVariable(variable.Sort, symbolName);

            var equal = (BoolHandle)ExpressionFactory.Equal(symbolWrapper, value);
            this.smtSolver.AddAssertion(this.NameProvider, equal);
        }
    }
}
