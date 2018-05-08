﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AskTheCode.ControlFlowGraphs;
using AskTheCode.ControlFlowGraphs.Heap;
using AskTheCode.SmtLibStandard;
using AskTheCode.SmtLibStandard.Handles;
using CodeContractsRevival.Runtime;

namespace AskTheCode.PathExploration.Heap
{
    public partial class ArrayTheorySymbolicHeap
    {
        internal class VariableState
        {
            public const int NullId = 0;
            public const int NullValue = 0;
            public static readonly VariableState Null = new VariableState(NullId, NullValue, true, NullValue);

            private VariableState(int id, IntHandle representation, bool canBeNull, int? value)
            {
                this.Id = id;
                this.Representation = representation;
                this.CanBeNull = canBeNull;
                this.Value = value;
            }

            public int Id { get; }

            public IntHandle Representation { get; }

            public bool CanBeNull { get; }

            public int? Value { get; }

            public bool IsNull => this.Id == NullId;

            public bool IsInput => this.Value == null;

            public bool IsExplicitlyAllocated => !this.IsInput && !this.IsNull;

            public static VariableState CreateInput(int id, NamedVariable namedVariable, bool canBeNull)
            {
                Contract.Requires(namedVariable.Sort == Sort.Int);

                return new VariableState(id, (IntHandle)namedVariable, canBeNull, null);
            }

            public static VariableState CreateValue(int id, int value)
            {
                return new VariableState(id, value, false, value);
            }

            public VariableState WithCanBeNull(bool canBeNull)
            {
                return new VariableState(this.Id, this.Representation, canBeNull, this.Value);
            }

            public override string ToString()
            {
                if (this == Null)
                {
                    return $"[{this.Id}] NULL";
                }
                else
                {
                    string nullInfo = (this.IsInput && !this.CanBeNull) ? ", NOT NULL" : "";
                    return $"[{this.Id}] {this.Representation}{nullInfo}";
                }
            }
        }

        private class HeapState
        {
            public static readonly HeapState ConflictState = new HeapState(null, null, null, null, -1, -1);

            public static readonly HeapState BasicState = ConstructBasicState();

            private readonly ImmutableSortedDictionary<int, VariableState> variableStates;
            private readonly ImmutableDictionary<VersionedVariable, int> variableToStateIdMap;
            private readonly ImmutableDictionary<int, ImmutableList<VariableMappingInfo>> stateIdToVariablesMap;
            private readonly ImmutableDictionary<IFieldDefinition, ArrayHandle<IntHandle, Handle>> fieldToVariableMap;
            private readonly int nextVariableStateId;
            private readonly int nextReferenceValue;

            private HeapState(
                ImmutableSortedDictionary<int, VariableState> variableStates,
                ImmutableDictionary<VersionedVariable, int> variableToStateIdMap,
                ImmutableDictionary<int, ImmutableList<VariableMappingInfo>> stateIdToVariablesMap,
                ImmutableDictionary<IFieldDefinition, ArrayHandle<IntHandle, Handle>> fieldToVariableMap,
                int nextVariableStateId,
                int nextReferenceValue)
            {
                this.variableStates = variableStates;
                this.variableToStateIdMap = variableToStateIdMap;
                this.stateIdToVariablesMap = stateIdToVariablesMap;
                this.fieldToVariableMap = fieldToVariableMap;
                this.nextVariableStateId = nextVariableStateId;
                this.nextReferenceValue = nextReferenceValue;
            }

            private enum VariableMappingKind
            {
                Equality,
                Assignment
            }

            public Builder ToBuilder() => new Builder(this);

            public VariableState GetVariableState(VersionedVariable variable)
            {
                if (this.variableToStateIdMap.TryGetValue(variable, out int stateId))
                {
                    return this.variableStates[stateId];
                }
                else
                {
                    return null;
                }
            }

            public ArrayHandle<IntHandle, Handle> GetFieldArray(IFieldDefinition field)
            {
                return this.fieldToVariableMap[field];
            }

            public ImmutableArray<BoolHandle> GetAssumptions()
            {
                var refFieldHandles = this.fieldToVariableMap
                    .Where(kvp => kvp.Key.IsReference())
                    .Select(kvp => (ArrayHandle<IntHandle, IntHandle>)kvp.Value.Expression)
                    .ToArray();

                return this.variableStates.Values
                    .Where(s => s.IsInput)
                    .Select((s) =>
                    {
                        // If there are no fields, only the object must be from the input heap
                        if (refFieldHandles.Length == 0)
                        {
                            if (s.CanBeNull)
                            {
                                return s.Representation <= VariableState.NullValue;
                            }
                            else
                            {
                                return s.Representation < VariableState.NullValue;
                            }
                        }

                        // Both the referenced object and all the objects referenced by it
                        // must be from the input heap (if not null)
                        var readConjuncts = new List<Expression>()
                        {
                            s.Representation < VariableState.NullValue
                        };

                        // TODO: Use only the fields present in the corresponding class
                        readConjuncts.AddRange(
                            refFieldHandles
                                .Select(h => (h.Select(s.Representation) <= VariableState.NullValue).Expression));

                        var readAnd = (BoolHandle)ExpressionFactory.And(readConjuncts.ToArray());

                        if (s.CanBeNull)
                        {
                            return s.Representation == VariableState.NullValue || readAnd;
                        }
                        else
                        {
                            return readAnd;
                        }
                    })
                    .ToImmutableArray();
            }

            public HeapState AllocateNew(
                VersionedVariable result,
                ISymbolicHeapContext context)
            {
                (var state, var newVarState) = this.MapToNewValueVariableState(result);
                if (state == ConflictState)
                {
                    return ConflictState;
                }

                var origVarState = this.GetVariableOrNull(result);
                if (origVarState != null)
                {
                    // Note that allocating the same variable twice would result in a conflict in SMT solver,
                    // eg. (assert (= 2 3))
                    context.AddAssertion(origVarState.Representation == newVarState.Representation);
                }

                return state;
            }

            public HeapState AssignReference(
                VersionedVariable result,
                VersionedVariable value,
                ISymbolicHeapContext context)
            {
                var resultState = this.GetVariableOrNull(result);
                var valueState = this.GetVariableOrNull(value);

                if (resultState == null && valueState == null)
                {
                    // From now on, we will handle them together, no need to assert their equality
                    (var algState, var newVarState) = this.MapToNewInputVariableState(value, context);
                    return algState.MapToVariableState(result, newVarState, VariableMappingKind.Assignment);
                }

                if (resultState == null || valueState == null)
                {
                    Contract.Assert(resultState != null || valueState != null);

                    // Add the newly added variable to the existing one; again, no assertion needed
                    return (resultState == null)
                        ? this.MapToVariableState(result, valueState, VariableMappingKind.Assignment)
                        : this
                            .UpdateMappingKind(result, VariableMappingKind.Assignment)
                            .MapToVariableState(value, resultState, VariableMappingKind.Equality);
                }

                Contract.Assert(resultState != null && valueState != null);

                if (IsNullStateAndVarState(resultState, valueState, out var varState))
                {
                    //Contract.Assert(varState == resultState);

                    if (!varState.CanBeNull)
                    {
                        // Variable said not to be null must be null, leading to a conflict
                        return ConflictState;
                    }
                    else
                    {
                        // Variable was assigned null
                        context.AddAssertion(varState.Representation == VariableState.Null.Representation);
                        return this.MapToVariableState(result, VariableState.Null, VariableMappingKind.Assignment);
                    }
                }

                // Assert the equality of the variables and unite them from now on
                // to reduce the number of generated assumptions
                context.AddAssertion(resultState.Representation == valueState.Representation);
                return this
                    .MapToBetterVariableState(result, resultState, value, valueState)
                    .UpdateMappingKind(result, VariableMappingKind.Assignment);
            }

            public HeapState AssertEquality(
                VersionedVariable left,
                VersionedVariable right,
                ISymbolicHeapContext context)
            {
                var leftState = this.GetVariableOrNull(left);
                var rightState = this.GetVariableOrNull(right);

                if (leftState == null && rightState == null)
                {
                    // From now on, we will handle them together, no need to assert their equality
                    (var algState, var newVarState) = this.MapToNewInputVariableState(left, context);
                    return algState.MapToVariableState(right, newVarState);
                }

                if (leftState == null || rightState == null)
                {
                    Contract.Assert(leftState != null || rightState != null);

                    // Add the newly added variable to the existing one; again, no assertion needed
                    return (leftState == null)
                        ? this.MapToVariableState(left, rightState)
                        : this.MapToVariableState(right, leftState);
                }

                Contract.Assert(leftState != null && rightState != null);

                if (IsNullStateAndVarState(leftState, rightState, out var varState))
                {
                    if (!varState.CanBeNull)
                    {
                        // Variable said not to be null must be null, leading to a conflict
                        return ConflictState;
                    }
                    else
                    {
                        var versionedVar = (varState == leftState) ? left : right;

                        // Variable must be null
                        context.AddAssertion(varState.Representation == VariableState.Null.Representation);
                        return this.MapToVariableState(versionedVar, VariableState.Null);
                    }
                }

                // Assert the equality of the variables and unite them from now on
                // to reduce the number of generated assumptions
                context.AddAssertion(leftState.Representation == rightState.Representation);
                return this.MapToBetterVariableState(left, leftState, right, rightState);
            }

            public HeapState AssertInequality(
                VersionedVariable left,
                VersionedVariable right,
                ISymbolicHeapContext context)
            {
                var leftState = this.GetVariableOrNull(left);
                var rightState = this.GetVariableOrNull(right);

                if (leftState == rightState && leftState != null)
                {
                    // Equal initialized variables are meant to be inequal, leading to a conflict
                    return ConflictState;
                }

                if (IsNullStateAndVarState(leftState, rightState, out var varState))
                {
                    if (varState.CanBeNull)
                    {
                        // Variable can't be null
                        context.AddAssertion(varState.Representation != VariableState.Null.Representation);
                        return this.UpdateVariableState(varState.WithCanBeNull(false));
                    }
                    else
                    {
                        // No more information provided, the variable is already known not to be null
                        return this;
                    }
                }

                HeapState resultState = this;

                // Initialize left variable, if needed
                if (leftState == null)
                {
                    (resultState, leftState) = resultState.MapToNewInputVariableState(left, context);
                }

                // Initialize right variable, if needed
                if (rightState == null)
                {
                    (resultState, rightState) = resultState.MapToNewInputVariableState(right, context);
                }

                if (leftState.Value == null || rightState.Value == null)
                {
                    // In the general case, assert the inequality
                    context.AddAssertion(leftState.Representation != rightState.Representation);
                }
                else
                {
                    // Two different states must be of different values, hence no need to assert their inequality
                    Contract.Assert(leftState.Value.Value != rightState.Value.Value);
                }

                return resultState;
            }

            public (HeapState newState, BoolHandle result) GetEqualityExpression(
                bool areEqual,
                VersionedVariable left,
                VersionedVariable right,
                ISymbolicHeapContext context)
            {
                (var stateWithLeft, var leftState) = this.GetOrAddVariable(left, context);
                (var resultState, var rightState) = stateWithLeft.GetOrAddVariable(right, context);

                BoolHandle result;
                if (leftState == rightState)
                {
                    // We know they are equal
                    result = areEqual;
                }
                else
                {
                    // We don't know directly, let the SMT solver decide it
                    result = areEqual
                        ? (leftState.Representation == rightState.Representation)
                        : (leftState.Representation != rightState.Representation);
                }

                return (resultState, result);
            }

            public HeapState ReadField(
                VersionedVariable result,
                VersionedVariable reference,
                IFieldDefinition field,
                ISymbolicHeapContext context)
            {
                (var algState, var refState) = this.SecureDereference(reference, context);
                if (algState == ConflictState)
                {
                    return ConflictState;
                }

                Expression resultVar;
                if (result.Variable.IsReference)
                {
                    // Secure that the result variable is initialized
                    VariableState resultState;
                    (algState, resultState) = algState.GetOrAddVariable(result, context);
                    resultVar = resultState.Representation;
                }
                else
                {
                    // Don't store scalar values in the state
                    resultVar = context.GetNamedVariable(result);
                }

                // Initialize the particular field
                ArrayHandle<IntHandle, Handle> fieldVar;
                (algState, fieldVar) = algState.GetOrAddFieldVariable(field, context);

                // Propagate the read to the SMT solver
                var selectAssert = (BoolHandle)ExpressionFactory.Equal(
                    resultVar,
                    fieldVar.Select(refState.Representation));
                context.AddAssertion(selectAssert);

                return algState;
            }

            public HeapState WriteField(
                VersionedVariable reference,
                IFieldDefinition field,
                Expression value,
                ISymbolicHeapContext context)
            {
                (var algState, var refState) = this.SecureDereference(reference, context);
                if (algState == ConflictState)
                {
                    return ConflictState;
                }

                Expression valExpr;
                if (value.Sort == References.Sort)
                {
                    if (!(value is FlowVariable valVar))
                    {
                        throw new NotSupportedException("Only versioned flow variables supported as references");
                    }

                    // Secure that the result variable is initialized
                    var versionedVal = context.GetVersioned(valVar);
                    VariableState valState;
                    (algState, valState) = algState.GetOrAddVariable(versionedVal, context);
                    valExpr = valState.Representation;
                }
                else
                {
                    // Don't store scalar values in the state
                    valExpr = value;
                }

                // Get current and new version of the field
                ArrayHandle<IntHandle, Handle> oldFieldVar, newFieldVar;
                (algState, oldFieldVar) = algState.GetOrAddFieldVariable(field, context);
                (algState, newFieldVar) = algState.CreateNewFieldVariableVersion(field, context);

                // Propagate the write to the SMT solver
                var storeAssert = (oldFieldVar == newFieldVar.Store(refState.Representation, (Handle)valExpr));
                context.AddAssertion(storeAssert);

                return algState;
            }

            private static HeapState ConstructBasicState()
            {
                var states = ImmutableSortedDictionary.CreateRange(new[]
                {
                    new KeyValuePair<int, VariableState>(VariableState.NullId, VariableState.Null)
                });
                var varStateMap = ImmutableDictionary.CreateRange(new[]
                {
                    new KeyValuePair<VersionedVariable, int>(VersionedVariable.Null, VariableState.NullId)
                });
                var stateVarsMap = ImmutableDictionary.CreateRange(new[]
                {
                    new KeyValuePair<int, ImmutableList<VariableMappingInfo>>(
                        VariableState.NullId,
                        ImmutableList.Create(new VariableMappingInfo(VersionedVariable.Null, VariableMappingKind.Equality)))
                });

                return new HeapState(
                    states,
                    varStateMap,
                    stateVarsMap,
                    ImmutableDictionary<IFieldDefinition, ArrayHandle<IntHandle, Handle>>.Empty,
                    1,
                    1);
            }

            private static bool IsNullStateAndVarState(
                VariableState leftState,
                VariableState rightState,
                out VariableState varState)
            {
                if (leftState == null || rightState == null)
                {
                    varState = null;
                    return false;
                }

                if (leftState == VariableState.Null && rightState != VariableState.Null)
                {
                    varState = rightState;
                    return true;
                }
                else if (rightState == VariableState.Null && leftState != VariableState.Null)
                {
                    varState = leftState;
                    return true;
                }
                else
                {
                    varState = null;
                    return false;
                }
            }

            private (HeapState algState, VariableState refState)
                SecureDereference(
                    VersionedVariable reference,
                    ISymbolicHeapContext context)
            {
                var algState = this;

                // Secure that the reference variable is initialized and not null
                var refState = algState.GetVariableOrNull(reference);
                if (refState == null)
                {
                    (algState, refState) = algState.MapToNewInputVariableState(
                        reference,
                        context,
                        canBeNull: false);

                    context.AddAssertion(refState.Representation != VariableState.Null.Representation);
                }
                else if (refState == VariableState.Null)
                {
                    // The statement wouldn't have executed due to null dereference
                    return (ConflictState, refState);
                }
                else if (refState.CanBeNull)
                {
                    context.AddAssertion(refState.Representation != VariableState.Null.Representation);

                    refState = refState.WithCanBeNull(false);
                    algState = algState.UpdateVariableState(refState);
                }

                return (algState, refState);
            }

            private (HeapState algState, ArrayHandle<IntHandle, Handle> fieldVar)
                GetOrAddFieldVariable(
                    IFieldDefinition field,
                    ISymbolicHeapContext context)
            {
                if (this.fieldToVariableMap.TryGetValue(field, out var fieldVar))
                {
                    return (this, fieldVar);
                }
                else
                {
                    return this.CreateNewFieldVariableVersion(field, context);
                }
            }

            private (HeapState algState, ArrayHandle<IntHandle, Handle> fieldVar)
                CreateNewFieldVariableVersion(
                    IFieldDefinition field,
                    ISymbolicHeapContext context)
            {
                var fieldSort = field.IsReference() ? Sort.Int : field.Sort;
                var newFieldVar = (ArrayHandle<IntHandle, Handle>)context.CreateVariable(
                    Sort.GetArray(Sort.Int, fieldSort),
                    field.ToString());

                var newFieldVarMap = this.fieldToVariableMap.SetItem(field, newFieldVar);
                var algState = new HeapState(
                    this.variableStates,
                    this.variableToStateIdMap,
                    this.stateIdToVariablesMap,
                    newFieldVarMap,
                    this.nextVariableStateId,
                    this.nextReferenceValue);

                return (algState, newFieldVar);
            }

            private HeapState UpdateVariableState(VariableState variableState)
            {
                var newVars = this.variableStates.SetItem(variableState.Id, variableState);
                return new HeapState(
                    newVars,
                    this.variableToStateIdMap,
                    this.stateIdToVariablesMap,
                    this.fieldToVariableMap,
                    this.nextVariableStateId,
                    this.nextReferenceValue);
            }

            private HeapState MapToVariableState(
                VersionedVariable variable,
                VariableState state,
                VariableMappingKind kind = VariableMappingKind.Equality)
            {
                Contract.Requires(this.variableStates[state.Id] == state);
                Contract.Requires(this.stateIdToVariablesMap.ContainsKey(state.Id));

                if (this.variableToStateIdMap.TryGetValue(variable, out int curStateId))
                {
                    if (curStateId == state.Id)
                    {
                        if (this.variableStates[curStateId] == state)
                        {
                            return this;
                        }
                        else
                        {
                            return this.UpdateVariableState(state).UpdateMappingKind(variable, kind);
                        }
                    }
                    else
                    {
                        // Update the states of all the variables pointing to the old one and erase it
                        var currentVars = this.stateIdToVariablesMap[curStateId];
                        var newVars = this.stateIdToVariablesMap[state.Id].AddRange(currentVars);
                        if (!currentVars.Any(v => v.Variable == variable))
                        {
                            // TODO: Consider turning it into a set to make this more effective
                            newVars = newVars.Add(new VariableMappingInfo(variable, kind));
                        }

                        if (state.IsExplicitlyAllocated && newVars.Count(v => v.Kind == VariableMappingKind.Equality) > 1)
                        {
                            // TODO: Think about the situation when the value is assigned in constructor

                            // The remaining variables are bound to be equal but can't be assigned the same value
                            // later as the instance won't be yet created
                            return ConflictState;
                        }

                        var newStates = this.variableStates.Remove(curStateId);
                        var newStateVarsMap = this.stateIdToVariablesMap
                            .Remove(curStateId)
                            .SetItem(state.Id, newVars);
                        var newVarStateMap = this.variableToStateIdMap.SetItems(
                            newVars.Select(v => new KeyValuePair<VersionedVariable, int>(v.Variable, state.Id)));

                        return new HeapState(
                            newStates,
                            newVarStateMap,
                            newStateVarsMap,
                            this.fieldToVariableMap,
                            this.nextVariableStateId,
                            this.nextReferenceValue);
                    }
                }
                else
                {
                    var newVarStateMap = this.variableToStateIdMap.Add(variable, state.Id);
                    var currentVars = this.stateIdToVariablesMap.TryGetValue(state.Id, out var vars)
                        ? vars
                        : ImmutableList<VariableMappingInfo>.Empty;
                    var varInfo = new VariableMappingInfo(variable, kind);
                    var newStateVarsMap = this.stateIdToVariablesMap.SetItem(state.Id, currentVars.Add(varInfo));

                    return new HeapState(
                        this.variableStates,
                        newVarStateMap,
                        newStateVarsMap,
                        this.fieldToVariableMap,
                        this.nextVariableStateId,
                        this.nextReferenceValue);
                }
            }

            private HeapState MapToBetterVariableState(
                VersionedVariable left,
                VariableState leftState,
                VersionedVariable right,
                VariableState rightState)
            {
                Contract.Requires(this.variableToStateIdMap[left] == leftState.Id);
                Contract.Requires(this.variableToStateIdMap[right] == rightState.Id);
                Contract.Requires(this.variableStates[leftState.Id] == leftState);
                Contract.Requires(this.variableStates[rightState.Id] == rightState);
                Contract.Requires(
                    leftState == rightState
                    || (leftState != VariableState.Null && rightState != VariableState.Null));

                if (leftState == rightState)
                {
                    return this;
                }

                if (!leftState.IsInput && !rightState.IsInput)
                {
                    Contract.Assert(leftState.Value.Value != rightState.Value.Value);

                    return ConflictState;
                }

                if (leftState.IsInput && rightState.IsInput)
                {
                    if (!rightState.CanBeNull && leftState.CanBeNull)
                    {
                        // Map to the state with stronger condition
                        return this.MapToVariableState(left, rightState);
                    }
                    else
                    {
                        // By default, map to the left state
                        return this.MapToVariableState(right, leftState);
                    }
                }

                // Get rid of the input state
                if (leftState.IsInput)
                {
                    Contract.Assert(!rightState.IsInput);

                    return this.MapToVariableState(left, rightState);
                }
                else
                {
                    Contract.Assert(rightState.IsInput);

                    return this.MapToVariableState(right, leftState);
                }
            }

            private (HeapState algState, VariableState refState)
                MapToNewInputVariableState(
                    VersionedVariable variable,
                    ISymbolicHeapContext context,
                    bool canBeNull = true)
            {
                var newVar = context.CreateVariable(Sort.Int, variable.ToString());
                var varState = VariableState.CreateInput(this.nextVariableStateId, newVar, canBeNull);
                var newVarStates = this.variableStates.Add(varState.Id, varState);
                var newStateVarsMap = this.stateIdToVariablesMap.SetItem(varState.Id, ImmutableList<VariableMappingInfo>.Empty);

                var algState = new HeapState(
                    newVarStates,
                    this.variableToStateIdMap,
                    newStateVarsMap,
                    this.fieldToVariableMap,
                    this.nextVariableStateId + 1,
                    this.nextReferenceValue);

                algState = algState.MapToVariableState(variable, varState);

                return (algState, varState);
            }

            private (HeapState state, VariableState result) MapToNewValueVariableState(VersionedVariable variable)
            {
                var varState = VariableState.CreateValue(this.nextVariableStateId, this.nextReferenceValue);
                var newVarStates = this.variableStates.Add(varState.Id, varState);
                var newStateVarsMap = this.stateIdToVariablesMap.SetItem(varState.Id, ImmutableList<VariableMappingInfo>.Empty);

                var algState = new HeapState(
                    newVarStates,
                    this.variableToStateIdMap,
                    newStateVarsMap,
                    this.fieldToVariableMap,
                    this.nextVariableStateId + 1,
                    this.nextReferenceValue + 1);

                algState = algState.MapToVariableState(variable, varState, VariableMappingKind.Assignment);

                return (algState, varState);
            }

            private HeapState UpdateMappingKind(VersionedVariable variable, VariableMappingKind kind)
            {
                int stateId = this.variableToStateIdMap[variable];
                var mappedVars = this.stateIdToVariablesMap[stateId];
                var mappedVar = mappedVars.Find(m => m.Variable == variable);

                if (mappedVar.Kind == kind)
                {
                    return this;
                }
                else
                {
                    var newMappedVars = mappedVars.Replace(mappedVar, mappedVar.WithKind(kind));
                    var newStateVarsMap = this.stateIdToVariablesMap.SetItem(stateId, newMappedVars);

                    return new HeapState(
                        this.variableStates,
                        this.variableToStateIdMap,
                        newStateVarsMap,
                        this.fieldToVariableMap,
                        this.nextVariableStateId,
                        this.nextReferenceValue);
                }
            }

            private (HeapState newState, VariableState varState) GetOrAddVariable(VersionedVariable variable, ISymbolicHeapContext context)
            {
                if (this.variableToStateIdMap.TryGetValue(variable, out var varStateId))
                {
                    return (this, this.variableStates[varStateId]);
                }
                else
                {
                    return this.MapToNewInputVariableState(variable, context);
                }
            }

            private VariableState GetVariableOrNull(VersionedVariable variable)
            {
                if (this.variableToStateIdMap.TryGetValue(variable, out var varStateId))
                {
                    return this.variableStates[varStateId];
                }
                else
                {
                    return null;
                }
            }

            private struct VariableMappingInfo
            {
                public VersionedVariable Variable;
                public VariableMappingKind Kind;

                public VariableMappingInfo(VersionedVariable variable, VariableMappingKind kind)
                {
                    this.Variable = variable;
                    this.Kind = kind;
                }

                public VariableMappingInfo WithKind(VariableMappingKind kind)
                {
                    return new VariableMappingInfo(this.Variable, kind);
                }
            }

            public class Builder
            {
                private readonly ImmutableSortedDictionary<int, VariableState>.Builder variableStates;
                private readonly ImmutableDictionary<VersionedVariable, int>.Builder variableToStateIdMap;
                private readonly ImmutableDictionary<int, ImmutableList<VariableMappingInfo>>.Builder stateIdToVariablesMap;
                private readonly ImmutableDictionary<IFieldDefinition, ArrayHandle<IntHandle, Handle>>.Builder fieldToVariableMap;
                private readonly int nextVariableStateId;
                private readonly int nextReferenceValue;

                private HeapState cachedState;

                public Builder(HeapState state)
                {
                    this.variableStates = state.variableStates.ToBuilder();
                    this.variableToStateIdMap = state.variableToStateIdMap.ToBuilder();
                    this.stateIdToVariablesMap = state.stateIdToVariablesMap.ToBuilder();
                    this.fieldToVariableMap = state.fieldToVariableMap.ToBuilder();
                    this.nextVariableStateId = state.nextVariableStateId;
                    this.nextReferenceValue = state.nextReferenceValue;

                    this.cachedState = state;
                }

                public HeapState ToState()
                {
                    if (this.cachedState == null)
                    {
                        this.cachedState = new HeapState(
                            this.variableStates.ToImmutable(),
                            this.variableToStateIdMap.ToImmutable(),
                            this.stateIdToVariablesMap.ToImmutable(),
                            this.fieldToVariableMap.ToImmutable(),
                            this.nextVariableStateId,
                            this.nextReferenceValue);
                    }

                    return this.cachedState;
                }
            }
        }
    }
}