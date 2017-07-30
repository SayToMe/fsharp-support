﻿using JetBrains.Annotations;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Caches2;
using JetBrains.ReSharper.Psi.FSharp.Tree;

namespace JetBrains.ReSharper.Psi.FSharp.Impl.Cache2.Parts
{
  internal class NestedModulePart : ModulePartBase<INestedModuleDeclaration>
  {
    public NestedModulePart([NotNull] INestedModuleDeclaration declaration, [NotNull] ICacheBuilder cacheBuilder)
      : base(declaration, FSharpImplUtil.GetNestedModuleShortName(declaration, cacheBuilder),
        ModifiersUtil.GetDecoration(declaration.AccessModifiers, declaration.AttributesEnumerable), cacheBuilder)
    {
    }

    public NestedModulePart(IReader reader) : base(reader)
    {
    }

    public override TypeElement CreateTypeElement()
    {
      return new FSharpModule(this);
    }

    protected override byte SerializationTag => (byte) FSharpPartKind.NestedModule;
  }
}