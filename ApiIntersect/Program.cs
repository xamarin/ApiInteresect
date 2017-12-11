//
// Program.cs
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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Ast;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Options;
using System.Diagnostics;

namespace ApiIntersect
{
	class MainClass
	{
		static HashSet<string> classBlacklist = new HashSet<string> ();
		static HashSet<string> classWhitelist = new HashSet<string> ();
		static HashSet<string> memberRemovalTypeWhitelist = new HashSet<string> ();

		//this isn't useful in contracts anyway, it's dead weight
		static bool keepInteropInfo = false;

		static bool stripSerializableAttribute = true;
		static bool redirectInteropServices = true;
		static bool keepInternalCtors = false;

		static int verbosity = 0;

		public static int Main (string [] args)
		{
			var intersections = new List<string> ();
			var except = new List<string> ();

			bool printHelp = args.Length == 0;

			string frameworkPath = null;

			string outputPath = Environment.CurrentDirectory;

			var refPaths = new List<string> ();

			var opts = new OptionSet {
				{ "i|intersect=", "Remove types and members that do not exist in this assembly", i => AddSeparated (intersections.Add, i) },
				{ "e|except=", "Remove types and members that exist in this assembly", i => AddSeparated (except.Add, i) },
				{ "b|blacklist=", "Remove this type and references to it", b => AddSeparated (a => classBlacklist.Add (a), b) },
				{ "f|frameworkPath=", "Filter types not accessible from this target framework", f => frameworkPath = f },
				{ "a|allow=", "Allow removing abstract members from this type", a => AddSeparated (x => memberRemovalTypeWhitelist.Add (a), a) },
				{ "k|keep-ctors", "Keep internal ctors", _ => keepInternalCtors = true },
				{ "m|keep-marshalling", "Keep marshalling and interop attributes", _ => keepInteropInfo = true },
				{ "o|output=", "Path to output the generated code", o => outputPath = o },
				{ "h|help", "Show help", _ => printHelp = true },
				{ "v|verbose", "Increase output verbosity", _ => verbosity++ },
				{ "r|refPath=", "Directory to resolve assembly references", r => refPaths.Add (r) }
			};

			try {
				List<string> remaining = opts.Parse (args);
				if (printHelp) {
					Console.WriteLine ("Usage: ApiIntersect MAINASSEMBLY [options]");
					Console.WriteLine ();
					opts.WriteOptionDescriptions (Console.Out);
					return 0;
				}

				if (remaining.Count > 0) {
					Console.Error.WriteLine ($"ERROR: Unknown argument {string.Join ("", remaining)}");
					return 1;
				}

				if (intersections.Count == 0) {
					Console.Error.WriteLine ($"ERROR: No intersection assemblies provided");
					return 1;
				}

			} catch (OptionException ex) {
				Console.Error.WriteLine ("ERROR: " + ex.Message);
				return 1;
			}

			var readerParameters = new ReaderParameters ();

			if (frameworkPath != null) {
				if (!File.Exists (Path.Combine (frameworkPath, "mscorlib.dll"))) {
					Console.Error.WriteLine ($"ERROR: no framework found at {frameworkPath}");
					return 1;
				}
				readerParameters.AssemblyResolver = new FrameworkAssemblyResolver (frameworkPath, refPaths);
				readerParameters.MetadataResolver = new HackMetadataResolver (readerParameters.AssemblyResolver);

				//PCLs don't have System.SerializableAttribute
				var corlib = readerParameters.AssemblyResolver.Resolve ("mscorlib");
				stripSerializableAttribute = corlib.MainModule.GetType ("System.SerializableAttribute") == null;

				//work around profile259 not having System.Runtime.InteropServices.dll
				try {
					redirectInteropServices = readerParameters.AssemblyResolver.Resolve ("System.Runtime.InteropServices") == null;
				} catch {
					redirectInteropServices = true;
				}
			}

			if (!keepInteropInfo) {
				BlacklistInteropMethods ();
			}

			if (verbosity > 1) {
				if (stripSerializableAttribute) {
					Console.WriteLine ("Stripping serialization info as SerializableAttribute was not round");
				}
				if (redirectInteropServices) {
					Console.WriteLine ("Redirecting UnmanagedType to mscorlib as System.Runtime.InteropServices.dll was not found");
				}
				if (keepInteropInfo) {
					Console.WriteLine ("Keeping marshalling/interop info");
				}
			}

			Directory.CreateDirectory (outputPath);


			Process (intersections, except, readerParameters, outputPath);

			return 0;
		}

		static void BlacklistInteropMethods ()
		{
			classBlacklist.Add ("ObjCRuntime.BlockProxyAttribute");
			classBlacklist.Add ("ObjCRuntime.DesignatedInitializerAttribute");
			classBlacklist.Add ("Foundation.ProtocolMemberAttribute");
		}

		static void AddSeparated (Action<string> add, string values)
		{
			foreach (var val in values.Split (new [] { ';' }, StringSplitOptions.RemoveEmptyEntries))
				add (val);
		}

		static void WriteWarning (string message)
		{
			Console.Error.WriteLine ("WARNING: " + message);
		}

		static void Process (List<string> intersections, List<string> exclusions, ReaderParameters readerParameters, string outputPath)
		{
			var lookup = new Dictionary<string, TypeDefinition []> ();

			if (verbosity > 0)
				Console.WriteLine ("Loading assemblies");

			for (int i = 0; i < intersections.Count; i++) {
				var asm = AssemblyDefinition.ReadAssembly (intersections [i], readerParameters);
				if (verbosity > 1)
					Console.WriteLine ($"  Loaded {asm}");
				AddTypesToMap (lookup, asm.MainModule.Types, intersections.Count, i);
			}

			if (exclusions.Count > 0) {
				if (verbosity > 0)
					Console.WriteLine ("Filtering excluded types");

				foreach (var excl in exclusions) {
					if (verbosity > 1)
						Console.WriteLine ($"Filtering types from {Path.GetFileNameWithoutExtension (excl)}");
					var asm = AssemblyDefinition.ReadAssembly (excl, readerParameters);
					foreach (var type in asm.MainModule.Types) {
						if (lookup.Remove (type.FullName) && verbosity > 2) {
							Console.WriteLine ($"  {type.FullName}");
						}
					}
				}
			}

			if (verbosity > 0)
				Console.WriteLine ("Intersecting types");

			foreach (var l in lookup) {
				for (int i = 0; i < l.Value.Length; i++) {
					if (l.Value [i] == null) {
						if (verbosity > 2 && !l.Key.Contains (">c__")) {
							var fx = Path.GetFileNameWithoutExtension (intersections [i]);
							Console.WriteLine ($"  {l.Key} missing from {fx}");
						}
						classBlacklist.Add (l.Key);
						break;
					}
				}
			}

			var types = new List<TypeDefinition []> (lookup.Values.Where (t => t [0] != null && !IsBlacklisted (t [0])));

			if (verbosity > 0)
				Console.WriteLine ("Intersecting members");

			var intersected = types.Select (Intersect).Where (t => t != null && !t.IsNested).OrderBy (t => t.FullName).ToList ();

			//now remove nested types. couldn't remove them earlier, because it altered their names
			if (verbosity > 2)
				Console.WriteLine ($"Removing blacklisted nested types");
			RemoveNestedTypes (types);

			if (verbosity > 0)
				Console.WriteLine ("Hiding the bodies");

			foreach (var type in intersected)
				DisposeOfTheBodies (type);

			if (verbosity > 0)
				Console.WriteLine ("Printing output");

			DumpTypes (intersected, Path.Combine (outputPath, "Contract"));

			DumpForwarders (intersected, Path.Combine (outputPath, "TypeForwarders.cs"));
		}

		static void AddTypesToMap (Dictionary<string, TypeDefinition []> lookup, IEnumerable<TypeDefinition> types, int count, int index)
		{
			foreach (var t in types) {
				TypeDefinition [] arr;
				if (index == 0 || !lookup.TryGetValue (t.FullName, out arr)) {
					arr = new TypeDefinition [count];
					lookup.Add (t.FullName, arr);
				}
				arr [index] = t;
				AddTypesToMap (lookup, t.NestedTypes, count, index);
			}
		}

		static TypeDefinition Intersect (TypeDefinition [] tds)
		{
			var type = tds [0];

			if (verbosity > 2)
				Console.WriteLine ($"Processing type {type.FullName}");

			if (verbosity > 2)
				Console.WriteLine ($"  Removing blacklisted attributes");
			ClearBlacklistedAttributes (type);

			if (stripSerializableAttribute) {
				type.Attributes = type.Attributes & ~TypeAttributes.Serializable;
			}

			if (verbosity > 2)
				Console.WriteLine ($"  Removing blacklisted interfaces");
			RemoveWhere (tds [0].Interfaces, IsBlacklisted, i => {
				if (verbosity > 3)
					Console.WriteLine ($"     {i.FullName}");
			});

			//remove interfaces that aren't implemented by all versions of the type
			if (verbosity > 2)
				Console.WriteLine ($"  Intersecting interfaces");
			Intersect (tds, t => t.Interfaces, TypeRefsMatch, (i, i2) => {
				if (type.IsInterface)
					WriteWarning ($"Can't remove base interface {i.Name} from interface {type.FullName}");
				if (verbosity > 3)
					Console.WriteLine ($"     {i.FullName}");
			});

			//remove inaccessible nested types, methods and fields
			if (verbosity > 2)
				Console.WriteLine ($"  Removing inaccessible methods");
			RemoveWhere (tds [0].Methods, ShouldExclude, m => {
				if (type.IsInterface && !memberRemovalTypeWhitelist.Contains (type.FullName))
					WriteWarning ($"Removing member {m.Name} from interface {type.FullName}");
				m.DeclaringType = null;
				if (verbosity > 3)
					Console.WriteLine ($"     {m.FullName}");
			});
			RemoveWhere (tds [0].Fields, ShouldExclude, null);

			//remove methods that don't exist in all versions of the assembly
			if (verbosity > 2)
				Console.WriteLine ($"  Intersecting methods");
			Intersect (tds, t => t.Methods, MethodsMatch, (m, t) => {
				var asm = t.Module.Assembly.Name;
				if (m.IsAbstract && !memberRemovalTypeWhitelist.Contains (type.FullName))
					WriteWarning ($"Removing abstract method {m.Name} from {type.FullName}  (not found in {asm})");
				if (m.Name == "Invoke" && m.DeclaringType.BaseType.FullName == "System.MulticastDelegate") {
					WriteWarning ($"Removing delegate invoke {m.Name} from {type.FullName}  (not found in {asm})");
				}
				m.DeclaringType = null;
				if (verbosity > 3)
					Console.WriteLine ($"     {m.FullName} (not found in {asm})");
			});

			//if there are no accessible ctors, add an internal one to prevent generating a public one
			//but still allow subclassing
			if (!keepInternalCtors) {
				const TypeAttributes staticClass = TypeAttributes.Sealed | TypeAttributes.Abstract;
				bool isStaticClass = (type.Attributes & staticClass) == staticClass;
				if (!type.IsValueType && !isStaticClass && type.BaseType != null && !type.Methods.Any (m => m.IsConstructor && IsAccessible (m))) {
					type.Methods.Add (new MethodDefinition (".ctor", MethodAttributes.Assembly, type) {
						IsSpecialName = true,
						IsRuntimeSpecialName = true,
					});
				}
			}

			//remove properties whose getters and setters are gone
			if (verbosity > 2)
				Console.WriteLine ($"  Removing empty properties");
			RemoveWhere (tds [0].Properties, (p) => {
				if (p.SetMethod != null && p.SetMethod.DeclaringType == null)
					p.SetMethod = null;
				if (p.GetMethod != null && p.GetMethod.DeclaringType == null)
					p.GetMethod = null;
				return p.SetMethod == null && p.GetMethod == null;
			}, (p) => {
				if (verbosity > 3)
					Console.WriteLine ($"     {p.FullName}");
			});

			//remove events whose adders and removers are gone
			if (verbosity > 2)
				Console.WriteLine ($"  Removing empty events");
			RemoveWhere (tds [0].Events, (e) => {
				if (e.AddMethod != null && e.AddMethod.DeclaringType == null)
					e.AddMethod = null;
				if (e.RemoveMethod != null && e.RemoveMethod.DeclaringType == null)
					e.RemoveMethod = null;
				return e.AddMethod == null && e.RemoveMethod == null;
			}, (e) => {
				if (verbosity > 3)
					Console.WriteLine ($"     {e.FullName}");
			});

			//remove other members that don't exist in all versions of the type
			Intersect (tds, t => t.Fields, FieldsMatch);
			Intersect (tds, t => t.Properties, PropertiesMatch);
			Intersect (tds, t => t.Events, EventsMatch);

			return type;
		}

		static bool IsAccessible (MethodDefinition m)
		{
			return m.IsPublic || m.IsFamily || m.IsFamilyAndAssembly || m.IsFamilyOrAssembly;
		}

		static void DisposeOfTheBodies (TypeDefinition t)
		{
			var corlib = t.Module.AssemblyResolver.Resolve ("System.Runtime");
			var niex = corlib.MainModule.GetType ("System.NotImplementedException");
			var niexCtor = niex.Methods.Single (m => m.IsConstructor && !m.HasParameters);

			TypeDefinition bt = null;
			if (t.BaseType != null) {
				bt = t.BaseType.Resolve ();
			}

			foreach (var m in t.Methods) {
				if (!m.IsAbstract) {
					m.Body = new MethodBody (m);
					var ilp = m.Body.GetILProcessor ();
					if (m.IsConstructor && !m.IsStatic) {
						var bc = FindBest (
							bt.Methods.Where (IsViableBaseCtor),
							(a, b) => a.Parameters.Count.CompareTo (b.Parameters.Count)
						);
						if (bc != null && bc.HasParameters) {
							CallWithDefaults (m, ilp, bc);
						}
					}
					ilp.Emit (OpCodes.Newobj, niexCtor);
					ilp.Emit (OpCodes.Throw);
				}

				//clear all as they get ignored by the C# compiler
				//after we removed the method bodies
				m.MethodReturnType.CustomAttributes.Clear ();

				ClearBlacklistedAttributes (m);

				foreach (var p in m.Parameters) {
					ClearBlacklistedAttributes (p);
				}
			}

			foreach (var p in t.Properties)
				ClearBlacklistedAttributes (p);

			foreach (var e in t.Events)
				ClearBlacklistedAttributes (e);

			foreach (var f in t.Fields) {
				ClearBlacklistedAttributes (f);
			}

			foreach (var inner in t.NestedTypes)
				DisposeOfTheBodies (inner);
		}

		static bool IsViableBaseCtor (MethodDefinition m)
		{
			if (!m.IsConstructor || m.IsStatic || ShouldExclude (m) || IsHardObsoleted (m))
				return false;
			return true;
		}

		static bool IsHardObsoleted (ICustomAttributeProvider c)
		{
			return c.CustomAttributes.Any (a =>
			                               a.AttributeType.FullName == "System.ObsoleteAttribute" &&
			                               a.ConstructorArguments.Count == 2 &&
			                               a.ConstructorArguments [1].Value.Equals (true));
		}

		static T FindBest<T>(IEnumerable<T> items, Comparison<T> comparer)
		{
			bool first = true;
			T best = default (T);
			foreach (var item in items) {
				if (first || comparer (best, item) > 0) {
					best = item;
					first = false;
				}
			}
			return best;
		}

		static void CallWithDefaults (MethodDefinition caller, ILProcessor ilp, MethodDefinition callee)
		{
			ilp.Emit (OpCodes.Ldarg_0);
			foreach (var val in callee.Parameters) {
				var pt = val.ParameterType;
				if (pt.IsValueType) {
					var v = new VariableDefinition (pt);
					caller.Body.Variables.Add (v);
					ilp.Emit (OpCodes.Ldloca_S, v);
					ilp.Emit (OpCodes.Initobj, pt);
					ilp.Emit (OpCodes.Ldloc, v);
				} else {
					ilp.Emit (OpCodes.Ldnull);
				}
			}
			ilp.Emit (OpCodes.Call, callee);
		}

		static bool IsBlacklistedProtocolMember (CustomAttribute a)
		{
			if (a.AttributeType.FullName != "Foundation.ProtocolMemberAttribute")
				return false;

			if (IsBlacklisted (a.Properties.FirstOrDefault (p => p.Name == "ParameterType")))
				return true;

			if (IsBlacklisted (a.Properties.FirstOrDefault (p => p.Name == "ReturnType")))
				return true;

			if (IsBlacklisted (a.Properties.FirstOrDefault (p => p.Name == "PropertyType")))
				return true;

			return false;
		}

		static void ClearBlacklistedAttributes (IMetadataTokenProvider obj)
		{
			var attProvider = obj as ICustomAttributeProvider;
			if (attProvider != null && attProvider.HasCustomAttributes) {
				var atts = attProvider.CustomAttributes;
				RemoveWhere (atts, a => {
					if (IsBlacklisted (a.AttributeType))
						return true;
					if (IsBlacklistedProtocolMember (a))
						return true;
					return false;
				}, null);
				foreach (var a in atts) {
					RemoveWhere (a.Properties, p => IsBlacklisted (p), null);
				}
			}

			//BROKEN: this doesn't actually work, because cecil just lazily loads the info again
			//instead, resolve the marshalling types to smcorlib and strip them out in the generated code
			/* 
			if (stripInteropInfo) {
				var marshalInfoProvider = obj as IMarshalInfoProvider;
				if (marshalInfoProvider != null && marshalInfoProvider.HasMarshalInfo) {
					marshalInfoProvider.MarshalInfo = null;
				}
			}
			*/
		}

		static void RemoveNestedTypes (IEnumerable<TypeDefinition[]> types)
		{
			foreach (var t in types) {
				if (verbosity > 2)
					Console.WriteLine ($"  {t[0].FullName}");

				Intersect (t, u => u.NestedTypes, TypeRefsMatch, (parent, removed) => {
					if (verbosity > 3)
						Console.WriteLine ($"     {removed.FullName}");
				});

				RemoveWhere (t[0].NestedTypes, IsBlacklisted, u => {
					if (verbosity > 3)
						Console.WriteLine ($"     {u.FullName}");
				});
			}
		}

		static bool IsBlacklisted (CustomAttributeNamedArgument p)
		{
			if (p.Argument.Value == null)
				return false;

			var tr = p.Argument.Value as TypeReference;
			if (tr != null) {
				return IsBlacklisted (tr);
			}
			var arr = p.Argument.Value as CustomAttributeArgument [];
			if (arr != null) {
				foreach (var val in arr) {
					tr = val.Value as TypeReference;
					if (tr != null && IsBlacklisted (tr)) {
						return true;
					}
				}
			}
			return false;
		}

		static void Intersect<T>(
			TypeDefinition [] tds,
			Func<TypeDefinition, Mono.Collections.Generic.Collection<T>> selector,
			Func<T, T, bool> areEqual,
			Action<T, TypeDefinition> removalCheck = null
			)
		{
			var collections = Array.ConvertAll (tds, t => selector (t));
			var c1 = collections [0];

			for (int i = c1.Count - 1; i >= 0; i--) {
				var item1 = c1 [i];

				for (int j = 1; j < collections.Length; j++) {
					var c2 = collections [j];
					if (!c2.Any (items2 => areEqual (item1, items2))) {
						if (removalCheck != null)
							removalCheck (item1, tds [j]);
						c1.RemoveAt (i);
						break;
					}
				}
			}
		}

		static void RemoveWhere<T>(Mono.Collections.Generic.Collection<T> list, Func<T, bool> predicate, Action<T> removalCheck = null)
		{
			for (int i = list.Count - 1; i >= 0; i--) {
				var item = list [i];
				if (predicate (item)) {
					if (removalCheck != null)
						removalCheck (item);
					list.RemoveAt (i);
				}
			}
		}

		static bool IsOrphanedDelegate (TypeDefinition t)
		{
			if (t.Name.Length < "IDelegate".Length || !t.Name.EndsWith ("Delegate", StringComparison.Ordinal))
				return false;

			string name = t.Name.Substring (0, t.Name.Length - "Delegate".Length);
			if (name.StartsWith ("I", StringComparison.Ordinal)) {
				name = name.Substring (1);
			}

			var owner = new TypeReference (t.Namespace, name, t.Module, t.Scope);

			if (owner.Resolve () != null && IsBlacklisted (owner)) {
				return true;
			}

			return false;
		}

		static bool IsOrphanedImplementation (TypeDefinition t)
		{
			var iface = t.Interfaces.FirstOrDefault (i => i.Namespace == t.Namespace && i.Name == "I" + t.Name);
			if (iface != null)
				return IsBlacklisted (iface);
			return false;
		}

		static bool IsBlacklisted (TypeReference t)
		{
			if (classWhitelist.Contains (t.FullName))
				return false;

			if (classBlacklist.Contains (t.FullName))
				return true;

			TypeDefinition tDef = null;
			try {
				tDef = t.Resolve ();
			} catch (Exception ex) {
				WriteWarning (ex.Message);
			}

			if (tDef != null) {

				if (tDef.IsNested && (tDef.IsNestedPrivate || tDef.IsNestedAssembly)) {
					if (verbosity > 1)
						Console.WriteLine ($"Blacklisting inaccessible nested type: {tDef}");
					classBlacklist.Add (tDef.FullName);
					return true;
				}

				if (tDef.IsNotPublic) {
					if (verbosity > 1)
						Console.WriteLine ($"Blacklisting nonpublic type: {tDef}");
					classBlacklist.Add (tDef.FullName);
					return true;
				}

				if (tDef.BaseType != null && IsBlacklisted (tDef.BaseType)) {
					if (verbosity > 1)
						Console.WriteLine ($"Blacklisting type due to base: {tDef}");
					classBlacklist.Add (tDef.FullName);
					return true;
				}

				if (IsOrphanedDelegate (tDef)) {
					if (verbosity > 1)
						Console.WriteLine ($"Blacklisting orphaned delegate: {tDef}");
					classBlacklist.Add (tDef.FullName);
					return true;
				}

				if (IsOrphanedImplementation (tDef)) {
					if (verbosity > 1)
						Console.WriteLine ($"Blacklisting orphaned Obj-C delegate: {tDef}");
					classBlacklist.Add (tDef.FullName);
					return true;
				}

				if (tDef.BaseType != null && tDef.BaseType.FullName == "System.MulticastDelegate") {
					var invoke = tDef.Methods.First (m => m.Name == "Invoke");
					if (ShouldExclude (invoke)) {
						if (verbosity > 1)
							Console.WriteLine ($"Blacklisting delegate with blacklisted arguments: {tDef}");
						classBlacklist.Add (tDef.FullName);
						return true;
					}
				}
				classWhitelist.Add (tDef.FullName);
				return false;
			}

			var gp = t as GenericParameter;
			if (gp != null) {
				foreach (var ct in gp.Constraints) {
					if (IsBlacklisted (ct)) {
						return true;
					}
				}
				classWhitelist.Add (t.FullName);
				return false;
			}

			var ts = t as TypeSpecification;
			if (ts != null) {
				var elementType = t.GetElementType ();
				bool blacklisted = IsBlacklisted (elementType);
				if (blacklisted) {
					if (verbosity > 1)
						Console.WriteLine ($"Blacklisting unresolved generic type: {t}");
					classBlacklist.Add (t.FullName);
				} else {
					classWhitelist.Add (t.Name);
				}

				return blacklisted;
			}

			if (verbosity > 1)
				Console.WriteLine ($"Blacklisting unresolvable type: {t}");

			classBlacklist.Add (t.FullName);

			return true;
		}

		static bool TypeRefsMatch (TypeReference ta, TypeReference tb)
		{
			return ta.FullName == tb.FullName;
		}

		static bool MethodsMatch (MethodDefinition om, MethodDefinition m)
		{
			if (om.Name != m.Name) {
				//HACK: for case where intersected assembly explicitly implements interface member but main doesn't
				//in this case we really should make the main implementation explicit too
				bool nameOkay = false;
				if (m.Name.IndexOf('.') > 0) {
					var ifaceMethod = m.Overrides.FirstOrDefault (o => m.DeclaringType.Interfaces.Any (i => TypeRefsMatch (i, o.DeclaringType)));
					if (ifaceMethod != null) {
						nameOkay =  ifaceMethod.Name == om.Name;
					}
				}
				if (!nameOkay)
					return false;
			}

			if (!TypeRefsMatch (om.ReturnType, m.ReturnType))
				return false;
			if (!om.HasParameters)
				return !m.HasParameters;
			if (om.Parameters.Count != m.Parameters.Count)
				return false;
			for (int i = 0; i < om.Parameters.Count; i++) {
				if (!TypeRefsMatch (om.Parameters [i].ParameterType, m.Parameters [i].ParameterType))
					return false;
			}
			return true;
		}

		static bool FieldsMatch (FieldDefinition of, FieldDefinition f)
		{
			return of.Name == f.Name && TypeRefsMatch (of.FieldType, f.FieldType);
		}

		static bool PropertiesMatch (PropertyDefinition op, PropertyDefinition p)
		{
			if (op.Name != p.Name)
				return false;
			if (!TypeRefsMatch (op.PropertyType, p.PropertyType))
				return false;
			if (!op.HasParameters)
				return !p.HasParameters;
			if (op.Parameters.Count != p.Parameters.Count)
				return false;
			for (int i = 0; i < op.Parameters.Count; i++) {
				if (!TypeRefsMatch (op.Parameters [i].ParameterType, p.Parameters [i].ParameterType))
					return false;
			}
			return true;
		}

		static bool EventsMatch (EventDefinition oe, EventDefinition e)
		{
			if (oe.Name != e.Name)
				return false;
			return TypeRefsMatch (oe.EventType, e.EventType);
		}

		static bool ShouldExclude (MethodDefinition d)
		{
			if (IsBlacklisted (d.ReturnType) || d.Parameters.Any (p => IsBlacklisted (p.ParameterType)))
				return true;
			
			//remove override methods if virtual is gone
			//this happens with e.g. XmlReader.Close in Profile259
			if (d.IsVirtual && d.IsReuseSlot) {
				MethodDefinition m = d;
				do {
					m = TypeMap.GetBaseMethodInTypeHierarchy (m);
					if (m == null) {
						if (verbosity > 2)
							Console.WriteLine ($"    Removing override {d.DeclaringType.FullName}.{d.Name} as base virtual is missing");
						return true;
					}
				} while (!m.IsNewSlot);
			}

			//don't remove explicitly implemented interface members
			if (d.Overrides.Any (o => d.DeclaringType.Interfaces.Any (i => TypeRefsMatch (i, o.DeclaringType)))) {
				return false;
			}

			if (keepInternalCtors && d.IsConstructor && (d.IsAssembly || d.IsPrivate)) {
				return false;
			}

			if (d.IsPrivate || !d.IsManaged || d.IsAssembly)
				return true;

			return false;
		}

		static bool ShouldExclude (FieldDefinition d)
		{
			if (IsBlacklisted (d.FieldType))
				return true;

			if (d.IsPrivate || d.IsAssembly)
				return true;
			return false;
		}

		static void DumpTypes (List<TypeDefinition> types, string baseDir)
		{
			if (types == null || !types.Any ()) {
				WriteWarning (
					"Sorry, the intersection between types in the different assemblies is empty. " +
					"May it be possible you were trying Bait-and-switch on types inheriting on dependencies? " +
					"In such case that technique won't work for this scenario, please disable it.");
				return;
			}

			var module = types.First ().Module;

			var context = new DecompilerContext (module);

			context.Settings = new DecompilerSettings {
				ShowXmlDocumentation = true,
				UsingDeclarations = true,
				FullyQualifyAmbiguousTypeNames = true,
				FoldBraces = true,
			};

			//Xamarin.iOS has AdErrorEventArgs and ADErrorEventArgs
			//which collide on case insensitive filesystem
			var fileCollision = new HashSet<string> (StringComparer.OrdinalIgnoreCase);

			foreach (var t in types) {
				var filename = t.Name;
				int i = 0;
				while (!fileCollision.Add (t.Namespace + "/" + filename)) {
					filename = t.Name + (++i);
				}

				var astBuilder = new AstBuilder (context);
				astBuilder.AddType (t);
				astBuilder.RunTransformations ();
				astBuilder.SyntaxTree.AcceptVisitor (new ContractVisitor (!keepInteropInfo));

				var dir = Path.Combine (baseDir, t.Namespace);
				var file = Path.Combine (dir, filename + ".cs");

				Directory.CreateDirectory (dir);

				using (var contract = new StreamWriter (file)) {
					astBuilder.GenerateCode (new PlainTextOutput (contract));
				}
			}
		}

		static void DumpForwarders (List<TypeDefinition> types, string file)
		{
			using (var forwarders = new StreamWriter (file)) {
				forwarders.WriteLine ("using System.Runtime.CompilerServices;");
				forwarders.WriteLine ();
				foreach (var t in types) {
					forwarders.WriteLine ($"[assembly: TypeForwardedToAttribute(typeof({FormatTypeName (t)}))]");
				}

			}
		}

		static string FormatTypeName (TypeReference td)
		{
			string s = td.FullName;
			if (!td.HasGenericParameters)
				return s;
			return s.Substring (0, s.IndexOf ('`')) + "<" + new string (',', td.GenericParameters.Count - 1) + ">";
		}

		class HackMetadataResolver : MetadataResolver
		{
			public HackMetadataResolver (IAssemblyResolver asmResolver)
				: base (asmResolver)
			{
			}

			//certain PCL profiles (*259*cough*) don't contain System.Runtime.InteropServices.dll
			//but the ILSpy decompiler resolves it directly during decompilation, even if we
			//stripped all references from the dll
			public override TypeDefinition Resolve (TypeReference type)
			{
				if (redirectInteropServices && type.FullName == "System.Runtime.InteropServices.UnmanagedType") {
					var corlib = AssemblyResolver.Resolve ("mscorlib");
					return corlib.MainModule.GetType (type.FullName);
				}

				try {
					return base.Resolve (type);
				} catch {
					WriteWarning ($"Failed to resolve type reference {type.FullName}");
					throw;
				}
			}
		}
	}
}
