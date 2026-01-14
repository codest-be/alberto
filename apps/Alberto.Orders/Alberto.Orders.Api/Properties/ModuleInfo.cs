[assembly: Module("Types")]

[assembly: DataLoaderModule("DataLoaders")]

[assembly: DataLoaderDefaults(
    GenerateInterfaces = true,
    ServiceScope = DataLoaderServiceScope.DataLoaderScope,
    AccessModifier = DataLoaderAccessModifier.PublicInterface)]
