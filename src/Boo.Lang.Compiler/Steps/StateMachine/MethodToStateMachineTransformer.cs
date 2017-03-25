﻿#region license
// Copyright (c) 2003-2017 Rodrigo B. de Oliveira (rbo@acm.org), Mason Wheeler
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without modification,
// are permitted provided that the following conditions are met:
// 
//     * Redistributions of source code must retain the above copyright notice,
//     this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright notice,
//     this list of conditions and the following disclaimer in the documentation
//     and/or other materials provided with the distribution.
//     * Neither the name of Rodrigo B. de Oliveira nor the names of its
//     contributors may be used to endorse or promote products derived from this
//     software without specific prior written permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
// THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
#endregion
using System.Collections.Generic;
using System.Linq;
using Boo.Lang.Compiler.Ast;
using Boo.Lang.Compiler.Steps.Generators;
using Boo.Lang.Compiler.TypeSystem;
using Boo.Lang.Compiler.TypeSystem.Builders;
using Boo.Lang.Compiler.TypeSystem.Generics;
using Boo.Lang.Compiler.TypeSystem.Internal;

namespace Boo.Lang.Compiler.Steps.StateMachine
{
    internal abstract class MethodToStateMachineTransformer : AbstractTransformerCompilerStep
    {
 
		protected readonly InternalMethod _method;

        protected InternalMethod _moveNext;

        protected IField _state;

        protected readonly GeneratorTypeReplacer _methodToStateMachineMapper = new GeneratorTypeReplacer();

        protected BooClassBuilder _stateMachineClass;

        protected BooMethodBuilder _stateMachineConstructor;

        protected Field _externalSelfField;

        protected readonly List<LabelStatement> _labels;

        protected readonly System.Collections.Generic.List<TryStatementInfo> _tryStatementInfoForLabels = new System.Collections.Generic.List<TryStatementInfo>();

        private readonly Dictionary<IEntity, InternalField> _mapping = new Dictionary<IEntity, InternalField>();

        private readonly Dictionary<IEntity, IEntity> _entityMapper = new Dictionary<IEntity, IEntity>();

        protected int _finishedStateNumber;

        protected MethodToStateMachineTransformer(CompilerContext context, InternalMethod method)
		{
			_labels = new List<LabelStatement>();
			_method = method;

			Initialize(context);
		}

		protected LexicalInfo LexicalInfo
		{
			get { return _method.Method.LexicalInfo; }
		}

        protected GenericParameterDeclaration[] _genericParams;

        protected MethodInvocationExpression _stateMachineConstructorInvocation;

		public override void Run()
		{
            _genericParams = _method.Method.DeclaringType.GenericParameters.Concat(_method.Method.GenericParameters).ToArray();
			CreateStateMachine();
		    PrepareConstructorCalls();
			PropagateReferences();
		}

        protected virtual IEnumerable<GenericParameterDeclaration> GetStateMachineGenericParams()
        {
            return _genericParams;
        }

        protected virtual void PrepareConstructorCalls()
        {
            _stateMachineConstructorInvocation = CodeBuilder.CreateGenericConstructorInvocation(
                (IType)_stateMachineClass.ClassDefinition.Entity,
                GetStateMachineGenericParams());            
        }

        protected ParameterDeclaration MapParamType(ParameterDeclaration parameter)
	    {
            if (parameter.Type.NodeType == NodeType.GenericTypeReference)
            {
                var gen = (GenericTypeReference)parameter.Type;
                var genEntityType = gen.Entity as IConstructedTypeInfo;
                if (genEntityType == null)
                    return parameter;
                var trc = new TypeReferenceCollection();
                foreach (var genArg in gen.GenericArguments)
                {
                    var replacement = genArg;
                    foreach (var genParam in _genericParams)
                        if (genParam.Name.Equals(genArg.Entity.Name))
                        {
                            replacement = new SimpleTypeReference(genParam.Name) {Entity = genParam.Entity};
                            break;
                        }
                    trc.Add(replacement);
                }
                parameter = parameter.CloneNode();
                gen = (GenericTypeReference)parameter.Type;
                gen.GenericArguments = trc;
                gen.Entity = new GenericConstructedType(genEntityType.GenericDefinition, trc.Select(a => a.Entity).Cast<IType>().ToArray());
            }
	        return parameter;
	    }

        protected abstract void PropagateReferences();

        private void CreateStateMachineConstructor()
		{
			_stateMachineConstructor = CreateConstructor(_stateMachineClass);
		}

        protected abstract void SetupStateMachine();

        protected abstract string StateMachineClassName {
            get;
        }

        protected virtual void CreateStateMachine()
		{
            _stateMachineClass = CodeBuilder.CreateClass(StateMachineClassName);
			_stateMachineClass.AddAttribute(CodeBuilder.CreateAttribute(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)));
			_stateMachineClass.LexicalInfo = this.LexicalInfo;
            foreach (var param in _genericParams)
            {
                var replacement = _stateMachineClass.AddGenericParameter(param.Name);
                _methodToStateMachineMapper.Replace((IType)param.Entity, (IType)replacement.Entity);
            }

		    SetupStateMachine();
            CreateStateMachineConstructor();
            CreateMoveNext();
			
			SaveStateMachineClass(_stateMachineClass.ClassDefinition);
		}

        protected abstract void SaveStateMachineClass(ClassDefinition cd);

        protected abstract void CreateMoveNext();

		protected void TransformParametersIntoFieldsInitializedByConstructor(Method generator)
		{
			foreach (ParameterDeclaration parameter in generator.Parameters)
			{
				var entity = (InternalParameter)parameter.Entity;
				if (entity.IsUsed)
				{
					var field = DeclareFieldInitializedFromConstructorParameter(_stateMachineClass, 
                                                                                _stateMachineConstructor,
					                                                            entity.Name,
					                                                            entity.Type,
                                                                                _methodToStateMachineMapper);
					_mapping[entity] = (InternalField)field.Entity;
				}
			}
		}

		protected void TransformLocalsIntoFields(Method stateMachine)
		{
			foreach (var local in stateMachine.Locals)
			{
				var entity = (InternalLocal)local.Entity;
				if (IsExceptionHandlerVariable(entity))
				{
					AddToMoveNextMethod(local);
					continue;
				}

				AddInternalFieldFor(entity);
			}
			stateMachine.Locals.Clear();
		}

		private void AddToMoveNextMethod(Local local)
		{
            var newLocal = new InternalLocal(local, _methodToStateMachineMapper.MapType(((InternalLocal)local.Entity).Type));
		    _entityMapper.Add(local.Entity, newLocal);
		    local.Entity = newLocal;
			_moveNext.Method.Locals.Add(local);
		}

		private void AddInternalFieldFor(InternalLocal entity)
		{
            Field field = _stateMachineClass.AddInternalField(UniqueName(entity.Name), _methodToStateMachineMapper.MapType(entity.Type));
			_mapping[entity] = (InternalField)field.Entity;
		}

		private bool IsExceptionHandlerVariable(InternalLocal local)
		{
			Declaration originalDeclaration = local.OriginalDeclaration;
			if (originalDeclaration == null) return false;
			return originalDeclaration.ParentNode is ExceptionHandler;
		}

		protected MethodInvocationExpression CallMethodOnSelf(IMethod method)
		{
            var entity = _stateMachineClass.Entity;
            var genParams = _stateMachineClass.ClassDefinition.GenericParameters;
            if (!genParams.IsEmpty)
            {
                var args = genParams.Select(gpd => gpd.Entity).Cast<IType>().ToArray();
                entity = new GenericConstructedType(entity, args);
                var mapping = new InternalGenericMapping(entity, args);
                method = mapping.Map(method);
            }
			return CodeBuilder.CreateMethodInvocation(
				CodeBuilder.CreateSelfReference(entity),
				method);
		}

        protected Field DeclareFieldInitializedFromConstructorParameter(BooClassBuilder type,
		                                                      BooMethodBuilder constructor,
		                                                      string parameterName,
		                                                      IType parameterType,
                                                              TypeReplacer replacer)
        {
            parameterType = replacer.MapType(parameterType);
			Field field = type.AddInternalField(UniqueName(parameterName), parameterType);
			InitializeFieldFromConstructorParameter(constructor, field, parameterName, parameterType);
			return field;
		}

        private void InitializeFieldFromConstructorParameter(BooMethodBuilder constructor,
		                                             Field field,
		                                             string parameterName,
		                                             IType parameterType)
		{
            ParameterDeclaration parameter = constructor.AddParameter(parameterName, parameterType);
			constructor.Body.Add(
				CodeBuilder.CreateAssignment(
					CodeBuilder.CreateReference(field),
					CodeBuilder.CreateReference(parameter)));
		}

	    private void OnTypeReference(TypeReference node)
	    {
            var type = (IType)node.Entity;
            node.Entity = _methodToStateMachineMapper.MapType(type);	        
	    }

	    public override void OnSimpleTypeReference(SimpleTypeReference node)
	    {
            OnTypeReference(node);
	    }

        public override void OnArrayTypeReference(ArrayTypeReference node)
        {
            base.OnArrayTypeReference(node);
            OnTypeReference(node);
        }

        public override void OnCallableTypeReference(CallableTypeReference node)
        {
            base.OnCallableTypeReference(node);
            OnTypeReference(node);
        }

        public override void OnGenericTypeReference(GenericTypeReference node)
	    {
            base.OnGenericTypeReference(node);
            OnTypeReference(node);
        }

        public override void OnGenericTypeDefinitionReference(GenericTypeDefinitionReference node)
        {
            base.OnGenericTypeDefinitionReference(node);
            OnTypeReference(node);
        }

        public override void OnReferenceExpression(ReferenceExpression node)
        {
            InternalField mapped;
            if (_mapping.TryGetValue(node.Entity, out mapped))
            {
                ReplaceCurrentNode(
                    CodeBuilder.CreateMemberReference(
                        node.LexicalInfo,
                        CodeBuilder.CreateSelfReference(_stateMachineClass.Entity),
                        mapped));
            }
        }

        public override void OnSelfLiteralExpression(SelfLiteralExpression node)
		{
			ReplaceCurrentNode(CodeBuilder.CreateReference(node.LexicalInfo, ExternalEnumeratorSelf()));
		}

		public override void OnSuperLiteralExpression(SuperLiteralExpression node)
		{
			var externalSelf = CodeBuilder.CreateReference(node.LexicalInfo, ExternalEnumeratorSelf());
			if (AstUtil.IsTargetOfMethodInvocation(node)) // super(...)
				ReplaceCurrentNode(CodeBuilder.CreateMemberReference(externalSelf, (IMethod)GetEntity(node)));
			else // super.Method(...)
				ReplaceCurrentNode(externalSelf);
		}

	    private IMethod RemapMethod(Node node, GenericMappedMethod gmm, GenericParameterDeclarationCollection genParams)
	    {
            var sourceMethod = gmm.SourceMember;
	        if (sourceMethod.GenericInfo != null)
	            throw new CompilerError(node, "Mapping generic methods in generators is not implemented yet");

	        var baseType = sourceMethod.DeclaringType;
	        var genericInfo = baseType.GenericInfo;
	        if (genericInfo == null)
	            throw new CompilerError(node, "Mapping generic nested types in generators is not implemented yet");

	        var genericArgs = ((IGenericArgumentsProvider)gmm.DeclaringType).GenericArguments;
	        var mapList = new List<IType>();
	        foreach (var arg in genericArgs)
	        {
	            var mappedArg = genParams.SingleOrDefault(gp => gp.Name == arg.Name);
	            if (mappedArg != null)
	                mapList.Add((IType)mappedArg.Entity);
	            else mapList.Add(arg);
	        }
	        var newType = (IConstructedTypeInfo)new GenericConstructedType(baseType, mapList.ToArray());
	        return (IMethod)newType.Map(sourceMethod);
	    }

        public override void OnMemberReferenceExpression(MemberReferenceExpression node)
        {
            base.OnMemberReferenceExpression(node);
            var gmm = node.Entity as GenericMappedMethod;
            if (gmm != null)
            {
                var genParams = _stateMachineClass.ClassDefinition.GenericParameters;
                if (genParams.IsEmpty)
                    return;
                node.Entity = RemapMethod(node, gmm, genParams);
            }
        }

        public override void OnDeclaration(Declaration node)
        {
            base.OnDeclaration(node);
            if (_entityMapper.ContainsKey(node.Entity))
                node.Entity = _entityMapper[node.Entity];
        }

		public override void OnMethodInvocationExpression(MethodInvocationExpression node)
		{
			var superInvocation = IsInvocationOnSuperMethod(node);
			base.OnMethodInvocationExpression(node);
			if (!superInvocation)
				return;

			var accessor = CreateAccessorForSuperMethod(node.Target);
			Bind(node.Target, accessor);
		}

		private IEntity CreateAccessorForSuperMethod(Expression target)
		{
			var superMethod = (IMethod)GetEntity(target);
			var accessor = CodeBuilder.CreateMethodFromPrototype(target.LexicalInfo, superMethod, TypeMemberModifiers.Internal, UniqueName(superMethod.Name));
			var accessorEntity = (IMethod)GetEntity(accessor);
			var superMethodInvocation = CodeBuilder.CreateSuperMethodInvocation(superMethod);
			foreach (var p in accessorEntity.GetParameters())
				superMethodInvocation.Arguments.Add(CodeBuilder.CreateReference(p));
			accessor.Body.Add(new ReturnStatement(superMethodInvocation));

			DeclaringTypeDefinition.Members.Add(accessor);
			return GetEntity(accessor);
		}

		protected string UniqueName(string name)
		{
			return Context.GetUniqueName(name);
		}

		protected TypeDefinition DeclaringTypeDefinition
		{
			get { return _method.Method.DeclaringType; }
		}

		private static bool IsInvocationOnSuperMethod(MethodInvocationExpression node)
		{
			if (node.Target is SuperLiteralExpression)
				return true;

			var target = node.Target as MemberReferenceExpression;
			return target != null && target.Target is SuperLiteralExpression;
		}

		private Field ExternalEnumeratorSelf()
		{
			if (null == _externalSelfField)
			{
				_externalSelfField = DeclareFieldInitializedFromConstructorParameter(
					_stateMachineClass,
					_stateMachineConstructor,
					"self_",
					_method.DeclaringType,
                    _methodToStateMachineMapper);
			}

			return _externalSelfField;
		}

        protected sealed class TryStatementInfo
		{
			internal TryStatement _statement;
			internal TryStatementInfo _parent;
			
			internal bool _containsYield;
			internal int _stateNumber = -1;
			internal Block _replacement;
			
			internal IMethod _ensureMethod;
		}

        protected readonly System.Collections.Generic.List<TryStatementInfo> _convertedTryStatements 
            = new System.Collections.Generic.List<TryStatementInfo>();
        protected readonly Stack<TryStatementInfo> _tryStatementStack = new Stack<TryStatementInfo>();
		
		public override bool EnterTryStatement(TryStatement node)
		{
			var info = new TryStatementInfo();
			info._statement = node;
			if (_tryStatementStack.Count > 0)
				info._parent = _tryStatementStack.Peek();
			_tryStatementStack.Push(info);
			return true;
		}
		
		protected virtual BinaryExpression SetStateTo(int num)
		{
			return CodeBuilder.CreateAssignment(CodeBuilder.CreateMemberReference(_state),
			                                    CodeBuilder.CreateIntegerLiteral(num));
		}
		
		public override void LeaveTryStatement(TryStatement node)
		{
			TryStatementInfo info = _tryStatementStack.Pop();
			if (info._containsYield) {
				ReplaceCurrentNode(info._replacement);
				TryStatementInfo currentTry = (_tryStatementStack.Count > 0) ? _tryStatementStack.Peek() : null;
				info._replacement.Add(node.ProtectedBlock);
				if (currentTry != null) {
					ConvertTryStatement(currentTry);
					info._replacement.Add(SetStateTo(currentTry._stateNumber));
				} else {
					// leave try block, reset state to prevent ensure block from being called again
					info._replacement.Add(SetStateTo(_finishedStateNumber));
				}
				BooMethodBuilder ensureMethod = _stateMachineClass.AddMethod("$ensure" + info._stateNumber, TypeSystemServices.VoidType, TypeMemberModifiers.Private);
				ensureMethod.Body.Add(info._statement.EnsureBlock);
				info._ensureMethod = ensureMethod.Entity;
				info._replacement.Add(CallMethodOnSelf(ensureMethod.Entity));
				_convertedTryStatements.Add(info);
			}
		}
		
		protected void ConvertTryStatement(TryStatementInfo currentTry)
		{
			if (currentTry._containsYield)
				return;
			currentTry._containsYield = true;
			currentTry._stateNumber = _labels.Count;
			var tryReplacement = new Block();
			//tryReplacement.Add(CreateLabel(tryReplacement));
			// when the MoveNext() is called while the enumerator is still in running state, don't jump to the
			// try block, but handle it like MoveNext() calls when the enumerator is in the finished state.
			_labels.Add(_labels[_finishedStateNumber]);
			_tryStatementInfoForLabels.Add(currentTry);
			tryReplacement.Add(SetStateTo(currentTry._stateNumber));
			currentTry._replacement = tryReplacement;
		}
		
		protected LabelStatement CreateLabel(Node sourceNode)
		{
			InternalLabel label = CodeBuilder.CreateLabel(sourceNode, "$state$" + _labels.Count);
			_labels.Add(label.LabelStatement);
			_tryStatementInfoForLabels.Add(_tryStatementStack.Count > 0 ? _tryStatementStack.Peek() : null);
			return label.LabelStatement;
		}
		
		protected virtual BooMethodBuilder CreateConstructor(BooClassBuilder builder)
		{
			BooMethodBuilder constructor = builder.AddConstructor();
			constructor.Body.Add(CodeBuilder.CreateSuperConstructorInvocation(builder.Entity.BaseType));
			return constructor;
		}
   }
}