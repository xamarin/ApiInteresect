//
// ContractVisitor.cs
//
// Author:
//       Michael Hutchinson <m.j.hutchinson@gmail.com>
//
// Copyright (c) 2015 Xamarin Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Linq;
using ICSharpCode.Decompiler.Ast;
using ICSharpCode.NRefactory.CSharp;
using Mono.Cecil;

namespace ApiIntersect
{
	class ContractVisitor : DepthFirstAstVisitor
	{
		readonly bool stripMarshalAtts;

		public ContractVisitor (bool stripMarshalAtts)
		{
			this.stripMarshalAtts = stripMarshalAtts;
		}

		const Modifiers PublicOrProtected = Modifiers.Protected | Modifiers.Public;

		public override void VisitAttribute (ICSharpCode.NRefactory.CSharp.Attribute attribute)
		{
			base.VisitAttribute (attribute);

			if (stripMarshalAtts && string.Equals (attribute.Type.ToString (), "global::System.Runtime.InteropServices.MarshalAsAttribute", StringComparison.Ordinal))
				attribute.Remove ();
		}

		public override void VisitAttributeSection (AttributeSection attributeSection)
		{
			base.VisitAttributeSection (attributeSection);
			if (attributeSection.Attributes.Count == 0)
				attributeSection.Remove ();
		}

		public override void VisitMethodDeclaration (MethodDeclaration methodDeclaration)
		{
			if (methodDeclaration.HasModifier (Modifiers.Extern)) {
				methodDeclaration.Remove ();
				return;
			}

			if (methodDeclaration.Name == ".dtor" || methodDeclaration.Name == "Finalize") {
				methodDeclaration.Remove ();
				return;
			}

			base.VisitMethodDeclaration (methodDeclaration);
		}

		public override void VisitConstructorDeclaration (ConstructorDeclaration constructorDeclaration)
		{
			var i = constructorDeclaration.Initializer;
			if (i.ConstructorInitializerType == ConstructorInitializerType.Base) {
				foreach (var a in i.Arguments) {
					var t = a.Annotation<TypeInformation> ();
					if (t == null)
						continue;
					
					var p = a as PrimitiveExpression;
					var n = a as NullReferenceExpression;

					if (p != null && p.Value.Equals (0) && t.ExpectedType.FullName == "System.IntPtr")
						a.ReplaceWith (new CastExpression (AstType.Create ("System.IntPtr"), new PrimitiveExpression (0)));
					else if (n != null)
						a.ReplaceWith (new CastExpression (AstType.Create (t.ExpectedType.FullName.Replace ('/', '.')), new NullReferenceExpression ()));

				}
			}
			base.VisitConstructorDeclaration (constructorDeclaration);
		}

		public override void VisitSimpleType (SimpleType simpleType)
		{
			base.VisitSimpleType (simpleType);

			var tr = simpleType.Annotation<TypeReference>();

			//fully qualify everything, because the decompiler is really bad at correct qualification
			if (tr != null) {
				simpleType.Identifier = "global::" + tr.FullName.Replace ('/', '.');
				var idx = simpleType.Identifier.IndexOf ('`');
				if (idx > 0) {
					simpleType.Identifier = simpleType.Identifier.Substring (0, idx);
				}
			}
		}
	}
}
