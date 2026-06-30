using Verbeam.Core.Models;
using Verbeam.Core.Services;

internal static class ProviderModelResponseBuilder
{
    public static async Task<IReadOnlyList<TranslationModelDescriptor>> BuildProviderModelsAsync(
        ProviderDescriptor descriptor,
        OllamaModelCatalog ollamaModels,
        LlamaCppArtifactStore llamaArtifacts,
        ApiSupplierStore apiSuppliers,
        ApiSupplierPresetCatalogService apiSupplierPresets,
        TranslationRouteStore routes,
        TranslationConfigurationCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (descriptor.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return catalog.EnrichModels(
                descriptor.Name,
                await ollamaModels.ListAsync(descriptor.Name, cancellationToken));
        }

        if (descriptor.Name.Equals("llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            var statuses = await llamaArtifacts.GetStatusesAsync(verifySha256: false, cancellationToken);
            var models = statuses
                .Select(status =>
                {
                    var name = Pick(status.ModelAlias, status.ModelId);
                    return new TranslationModelDescriptor(
                        descriptor.Name,
                        name,
                        status.DisplayName,
                        IsDefault: name.Equals(descriptor.DefaultModel, StringComparison.OrdinalIgnoreCase) ||
                            status.ModelId.Equals(descriptor.DefaultModel, StringComparison.OrdinalIgnoreCase),
                        IsInstalled: status.Exists && status.SizeMatches,
                        "llama-cpp-artifact");
                })
                .ToArray();

            if (models.Length > 0)
            {
                return catalog.EnrichModels(descriptor.Name, models);
            }

            var fallbackModel = Pick(descriptor.DefaultModel, descriptor.Name);
            return catalog.EnrichModels(descriptor.Name, new[]
            {
                new TranslationModelDescriptor(
                    descriptor.Name,
                    fallbackModel,
                    fallbackModel,
                    IsDefault: true,
                    IsInstalled: false,
                    "llama-cpp-artifact")
            });
        }

        if (descriptor.Name.Equals("api-compatible", StringComparison.OrdinalIgnoreCase))
        {
            // Surface EVERY configured supplier's models (each tagged with its supplier id/name so
            // the picker can group them), not just the active route's — so a newly added supplier
            // shows up in the model menu without having to be activated first. The active route's
            // (supplier, model) pair is marked IsDefault. Built directly rather than via
            // EnrichModels because that keys models by Name and would collide across suppliers that
            // expose same-named models.
            var route = await routes.GetAsync("default", cancellationToken);
            var activeSupplierId = route is not null &&
                route.Provider.Equals(descriptor.Name, StringComparison.OrdinalIgnoreCase)
                ? route.SupplierId
                : null;
            var activeModel = route?.Model;

            var models = new List<TranslationModelDescriptor>();
            foreach (var supplier in await apiSuppliers.ListAsync(cancellationToken))
            {
                var supplierName = Pick(supplier.Name, supplier.PresetId);
                var isActiveSupplier = !string.IsNullOrWhiteSpace(activeSupplierId) &&
                    supplier.Id.Equals(activeSupplierId, StringComparison.OrdinalIgnoreCase);

                var entries = supplier.ModelCatalog
                    .Select(model => (Id: model.Id, Name: Pick(model.DisplayName, model.Id)))
                    .ToList();
                // A supplier with no fetched catalog still appears via its active/default model.
                if (entries.Count == 0 && !string.IsNullOrWhiteSpace(supplier.ActiveModel))
                {
                    entries.Add((supplier.ActiveModel, supplier.ActiveModel));
                }

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id))
                    {
                        continue;
                    }

                    models.Add(new TranslationModelDescriptor(
                        descriptor.Name,
                        entry.Id,
                        entry.Name,
                        IsDefault: isActiveSupplier &&
                            (string.IsNullOrWhiteSpace(activeModel) ||
                             entry.Id.Equals(activeModel, StringComparison.OrdinalIgnoreCase)),
                        IsInstalled: true,
                        "api-supplier",
                        SupplierId: supplier.Id,
                        SupplierName: supplierName));
                }
            }

            return models;
        }

        if (descriptor.Name.Equals("deepl", StringComparison.OrdinalIgnoreCase))
        {
            var defaultModel = Pick(descriptor.DefaultModel, "default");
            return catalog.EnrichModels(descriptor.Name, new[]
            {
                new TranslationModelDescriptor(
                    descriptor.Name,
                    "default",
                    "DeepL default",
                    IsDefault: defaultModel.Equals("default", StringComparison.OrdinalIgnoreCase),
                    IsInstalled: true,
                    "deepl",
                    IsRecommended: true,
                    RecommendedUse: "high-quality text translation"),
                new TranslationModelDescriptor(
                    descriptor.Name,
                    "quality_optimized",
                    "DeepL quality optimized",
                    IsDefault: defaultModel.Equals("quality_optimized", StringComparison.OrdinalIgnoreCase),
                    IsInstalled: true,
                    "deepl",
                    RecommendedUse: "next-gen translation quality when supported")
            });
        }

        var model = Pick(descriptor.DefaultModel, descriptor.Name);
        return catalog.EnrichModels(descriptor.Name, new[]
        {
            new TranslationModelDescriptor(
                descriptor.Name,
                model,
                model,
                IsDefault: true,
                IsInstalled: true,
                "provider")
        });
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
