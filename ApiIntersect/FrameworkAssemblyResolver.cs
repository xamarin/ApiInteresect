using System;
using Mono.Cecil;
using System.Collections.Generic;
using System.IO;

namespace ApiIntersect
{
	class FrameworkAssemblyResolver : IAssemblyResolver
	{
		readonly Dictionary<AssemblyNameReference, AssemblyDefinition> assemblies
			= new Dictionary<AssemblyNameReference, AssemblyDefinition> (new AssemblyNameReferenceComparer ());
		readonly ReaderParameters parameters;

		public FrameworkAssemblyResolver (string frameworkDirectory, List<string> refPaths = null, ReaderParameters parameters = null)
		{
			this.parameters = parameters ?? new ReaderParameters ();
			this.parameters.AssemblyResolver = this;

			foreach (var asm in Directory.GetFiles (frameworkDirectory, "*.dll"))
				AddAssembly (asm);
			
			var facadesDir = Path.Combine (frameworkDirectory, "Facades");
			if (Directory.Exists (facadesDir))
				foreach (var asm in Directory.GetFiles (facadesDir, "*.dll"))
					AddAssembly (asm);

			if (refPaths != null)
				foreach (var r in refPaths)
					foreach (var asm in Directory.GetFiles (r, "*.dll"))
						AddAssembly (asm);
		}

		public bool ThrowOnResolveFailure { get; set; } = true;

		public AssemblyDefinition Resolve (string fullName)
		{
			return Resolve (AssemblyNameReference.Parse (fullName));
		}

		public AssemblyDefinition Resolve (AssemblyNameReference name)
		{
			return Resolve (name, parameters);
		}

		public AssemblyDefinition Resolve (string fullName, ReaderParameters parameters)
		{
			return Resolve (AssemblyNameReference.Parse (fullName), parameters);
		}

		public AssemblyDefinition Resolve (AssemblyNameReference name, ReaderParameters parameters)
		{
			AssemblyDefinition asm;
			if (!assemblies.TryGetValue (name, out asm)) {
				if (ThrowOnResolveFailure) {
					throw new Exception ($"Could not resolve {name}");
				} else {
					Console.Error.WriteLine ($"WARNING: Could not resolve {name}");
				}
			}
			return asm;
		}

		void AddAssembly (string filename)
		{
			var asm = AssemblyDefinition.ReadAssembly (filename, parameters);
			if (!assemblies.ContainsKey (asm.Name))
				assemblies.Add (asm.Name, asm);
		}
	}

	class AssemblyNameReferenceComparer : IEqualityComparer<AssemblyNameReference>
	{
		public bool Equals (AssemblyNameReference x, AssemblyNameReference y)
		{
			//ignore versions for now
			return x.Name.Equals (y.Name);
		}

		public int GetHashCode (AssemblyNameReference obj)
		{
			return obj.Name.GetHashCode ();
		}
	}
}