using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AcceptCrypto.Service.Api;

/// <summary>Documents the API key scheme and marks the operations that need it (FR-21).</summary>
internal sealed class ApiKeyOpenApiTransformer : IOpenApiDocumentTransformer, IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "AcceptCrypto.NET merchant API";
        document.Info.Description =
            "Non-custodial USDC payments: invoices, on-chain payment tracking and signed webhooks. "
            + "Authenticate with the X-Api-Key header. Errors are RFC 9457 Problem Details with a `code` extension. "
            + "Amounts are decimal strings in USDC (max 6 decimals).";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeyAuthenticationHandler.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthenticationHandler.HeaderName,
            Description = "Merchant API key (configuration Merchant:ApiKey).",
        };
        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any())
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(ApiKeyAuthenticationHandler.SchemeName, context.Document)] = [],
            });
        }

        return Task.CompletedTask;
    }
}
