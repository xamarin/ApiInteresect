// from Mono.Linker.Steps/TypeMapStep.cs

using System.Collections.Generic;
using Mono.Cecil;

namespace ApiIntersect
{
	class TypeMap
	{
		public static MethodDefinition GetBaseMethodInTypeHierarchy (MethodDefinition method)
		{
			return GetBaseMethodInTypeHierarchy (method.DeclaringType, method);
		}

		static MethodDefinition GetBaseMethodInTypeHierarchy (TypeDefinition type, MethodDefinition method)
		{
			TypeDefinition @base = GetBaseType (type);
			while (@base != null) {
				MethodDefinition base_method = TryMatchMethod (@base, method);
				if (base_method != null)
					return base_method;

				@base = GetBaseType (@base);
			}

			return null;
		}

		static TypeDefinition GetBaseType (TypeDefinition type)
		{
			if (type == null || type.BaseType == null)
				return null;

			return type.BaseType.Resolve ();
		}

		static MethodDefinition TryMatchMethod (TypeDefinition type, MethodDefinition method)
		{
			if (!type.HasMethods)
				return null;

			Dictionary<string, string> gp = null;
			foreach (MethodDefinition candidate in type.Methods) {
				if (MethodMatch (candidate, method, ref gp))
					return candidate;
				if (gp != null)
					gp.Clear ();
			}

			return null;
		}

		static bool MethodMatch (MethodDefinition candidate, MethodDefinition method, ref Dictionary<string, string> genericParameters)
		{
			if (!candidate.IsVirtual)
				return false;

			if (candidate.HasParameters != method.HasParameters)
				return false;

			if (candidate.Name != method.Name)
				return false;

			if (candidate.HasGenericParameters != method.HasGenericParameters)
				return false;

			// we need to track what the generic parameter represent - as we cannot allow it to
			// differ between the return type or any parameter
			if (!TypeMatch (candidate.ReturnType, method.ReturnType, ref genericParameters))
				return false;

			if (!candidate.HasParameters)
				return true;

			var cp = candidate.Parameters;
			var mp = method.Parameters;
			if (cp.Count != mp.Count)
				return false;

			for (int i = 0; i < cp.Count; i++) {
				if (!TypeMatch (cp [i].ParameterType, mp [i].ParameterType, ref genericParameters))
					return false;
			}

			return true;
		}

		static bool TypeMatch (IModifierType a, IModifierType b, ref Dictionary<string, string> gp)
		{
			if (!TypeMatch (a.ModifierType, b.ModifierType, ref gp))
				return false;

			return TypeMatch (a.ElementType, b.ElementType, ref gp);
		}

		static bool TypeMatch (TypeSpecification a, TypeSpecification b, ref Dictionary<string, string> gp)
		{
			var gita = a as GenericInstanceType;
			if (gita != null)
				return TypeMatch (gita, (GenericInstanceType)b, ref gp);

			var mta = a as IModifierType;
			if (mta != null)
				return TypeMatch (mta, (IModifierType)b, ref gp);

			return TypeMatch (a.ElementType, b.ElementType, ref gp);
		}

		static bool TypeMatch (GenericInstanceType a, GenericInstanceType b, ref Dictionary<string, string> gp)
		{
			if (!TypeMatch (a.ElementType, b.ElementType, ref gp))
				return false;

			if (a.HasGenericArguments != b.HasGenericArguments)
				return false;

			if (!a.HasGenericArguments)
				return true;

			var gaa = a.GenericArguments;
			var gab = b.GenericArguments;
			if (gaa.Count != gab.Count)
				return false;

			for (int i = 0; i < gaa.Count; i++) {
				if (!TypeMatch (gaa [i], gab [i], ref gp))
					return false;
			}

			return true;
		}

		static bool TypeMatch (TypeReference a, TypeReference b, ref Dictionary<string, string> gp)
		{
			var gpa = a as GenericParameter;
			if (gpa != null) {
				if (gp == null)
					gp = new Dictionary<string, string> ();
				string match;
				if (!gp.TryGetValue (gpa.FullName, out match)) {
					// first use, we assume it will always be used this way
					gp.Add (gpa.FullName, b.ToString ());
					return true;
				}
				// re-use, it should match the previous usage
				return match == b.ToString ();
			}

			if (a is TypeSpecification || b is TypeSpecification) {
				if (a.GetType () != b.GetType ())
					return false;

				return TypeMatch ((TypeSpecification)a, (TypeSpecification)b, ref gp);
			}

			return a.FullName == b.FullName;
		}
	}
}