# API Intersection Tools

The repository contains ApiIntersect, a tool to generate contract assemblies from the
intersection of one or more reference/framework assemblies, and also generates facade
assemblies that allow the types to be resolved to the real framework types at runtime.

It also contains scripts that use the tool to generate intersection contract assemblies
and publish them to NuGet.

## [ApiIntersect](ApiIntersect)

### Usage

      Usage: ApiIntersect MAINASSEMBLY [options]
      
      -i, --intersect=VALUE      Remove types and members that do not exist in this
                                    assembly
      -e, --except=VALUE         Remove types and members that exist in this
                                    assembly
      -b, --blacklist=VALUE      Remove this type and references to it
      -f, --frameworkPath=VALUE  Filter types not accessible from this target
                                    framework
      -a, --allow=VALUE          Allow removing abstract members from this type
      -o, --output=VALUE         Path to output the generated code
      -h, --help                 Show help
      -v, --verbose              Increase output verbosity

### What it does

1. The main assembly is loaded
2. Intersection assemblies and exception assemblies are loaded
3. A type blacklist is constructed from the provided values.
4. Types in the main assembly are removed and blacklisted if they are nonpublic, do not exist in the
   intersection assemblies, exist in the exception assemblies, ot or are blacklisted
5. Members and attributes are removed from the main assembly if they are nonpublic, do not exist in the
   intersection assemblies, or reference blacklisted types or types that do not exist in the specified framework
6. Method bodies in the main assembly are replaced by stubs that throw NotImplementedException
7. The main assembly is decompiled into C# and emitted as a file called `Contract.cs`
8. Type forwarders for all of the remaining types are emitted in a file called `TypeForwarders.cs`

## Exclusions

Exclusions allow creating narrow intersections that are compatible with broader intersections.
For example, if the intersection of X=A+B+C is excluded from the intersection of Y=A+B, then
assemblies that reference X can be referenced from assemblies that reference Y.

## Blacklisting

Blacklisting serves two purposes: removing types that are not needed, and removing types
that are incompatible, for example interfaces or abstract classes where the abstract members
differ. In such cases, subclasses of the type will not be compatible with all of the
intesected assemblies.

## Whitelisting

Whitelisting offers a way to suppressing the warnings about incompatible abstract types.
This is useful when you have reviewed the type and determined that the user is not expected
to subclass it.

### How To Create a Bait-and-Switch NuGet

1. Use ApiIntersect to generate contract and forwarder source from an assembly that exists in multiple frameworks
2. Create a PCL library from `Contract.cs` and compile it into a contract assembly
3. For each of the original frameworks, create a library from `TypeForwarders.cs` that has the same assembly name
   as the contract assembly
4. Create a NuGet that uses the contract assembly as the PCL library and the forwarders as the implementations


