using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;

namespace RaceTrace.Api.Infrastructure;

public static class ApiContractExtensions
{
    public static IServiceCollection AddRaceTraceOpenApi(this IServiceCollection services)
    {
        var actions = typeof(ApiContractExtensions).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type)).SelectMany(type => type.GetMethods()).ToArray();
        var responseTypes = ContractTypes(actions.SelectMany(action => action.GetCustomAttributes<ProducesResponseTypeAttribute>()).Select(response => response.Type));
        responseTypes.ExceptWith(ContractTypes(actions.SelectMany(action => action.GetParameters()).Select(parameter => parameter.ParameterType)));
        // OpenAPI uses HTTP JSON options, whereas controllers use MVC JSON options.
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        });
        services.AddOpenApi(options =>
        {
            options.AddSchemaTransformer((schema, context, _) =>
            {
                var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
                // Response-only DTO getters are always serialized, including null/default values.
                // Do not impose response requiredness on request DTOs with optional constructor arguments.
                if (responseTypes.Contains(type) && schema.Properties is not null)
                {
                    schema.Required ??= new HashSet<string>();
                    foreach (var property in context.JsonTypeInfo.Properties.Where(property => property.Get is not null && property.ShouldSerialize is null))
                        if (schema.Properties.ContainsKey(property.Name)) schema.Required.Add(property.Name);
                }
                // Responses serialize numbers as JSON numbers. MVC still accepts numeric strings on input.
                if (type == typeof(int) || type == typeof(long) || type == typeof(decimal) || type == typeof(double) || type == typeof(float))
                {
                    schema.Type &= ~JsonSchemaType.String;
                    schema.Pattern = null;
                }
                if (typeof(JsonNode).IsAssignableFrom(type))
                {
                    schema.Description = "Dynamic JSON from the existing database/provider contract; properties are intentionally not invented.";
                    if (type == typeof(JsonObject))
                    {
                        schema.Type = JsonSchemaType.Object;
                        schema.AdditionalPropertiesAllowed = true;
                    }
                }
                var values = context.JsonPropertyInfo?.AttributeProvider?
                    .GetCustomAttributes(typeof(ApiContractValuesAttribute), true)
                    .OfType<ApiContractValuesAttribute>().SingleOrDefault();
                if (values is not null)
                {
                    schema.Enum = values.Values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
                    if ((schema.Type & JsonSchemaType.Null) != 0) schema.Enum.Add(null!);
                }
                return Task.CompletedTask;
            });
            options.AddOperationTransformer(async (operation, context, token) =>
            {
                if (context.Description.ActionDescriptor is not ControllerActionDescriptor action) return;
                ApiQueryContracts.Add(operation, action.ControllerName, action.ActionName);
                var error = await context.GetOrCreateSchemaAsync(typeof(Models.ApiErrorResponse), cancellationToken: token);
                var validation = await context.GetOrCreateSchemaAsync(typeof(ValidationProblemDetails), cancellationToken: token);
                operation.Responses!["400"] = new OpenApiResponse
                {
                    Description = "Invalid query, payload or model binding. Framework validation uses Problem Details; domain validation uses the API error envelope.",
                    Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = new OpenApiSchema { OneOf = [error, validation] } } }
                };
                foreach (var code in new[] { "401", "403", "500", "503" })
                    operation.Responses!.TryAdd(code, new OpenApiResponse
                    {
                        Description = "API access, database or upstream error.",
                        Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = error } }
                    });
                foreach (var code in action.ControllerName.StartsWith("Admin", StringComparison.Ordinal) ? new[] { "404", "409", "422" } : new[] { "404" })
                    operation.Responses!.TryAdd(code, new OpenApiResponse
                    {
                        Description = "Requested resource unavailable, conflicting operation or invalid imported data.",
                        Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = error } }
                    });
                if (action.ControllerName == "Rounds" && action.ActionName == "Image")
                {
                    operation.Responses!["200"] = new OpenApiResponse
                    {
                        Description = "Round image bytes.",
                        Content = new Dictionary<string, OpenApiMediaType> { ["image/*"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } } }
                    };
                    operation.Responses["302"] = new OpenApiResponse { Description = "Redirect to stored round image." };
                    operation.Responses["404"] = new OpenApiResponse { Description = "Image not available." };
                }
                if (action.ControllerName == "AdminFantasy" && action.ActionName == "SyncHtml")
                    operation.RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType> { ["text/html"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String } } }
                    };
                if (action.ControllerName == "ChronoRaceLive")
                    operation.Responses!["204"] = new OpenApiResponse { Description = "The timing provider returned no payload." };
                if (action.MethodInfo.GetCustomAttribute<RequestSizeLimitAttribute>() is not null)
                    operation.Responses!["413"] = new OpenApiResponse { Description = "Uploaded request exceeds the configured size limit." };
            });
        });
        return services;
    }

    private static HashSet<Type> ContractTypes(IEnumerable<Type> roots)
    {
        var found = new HashSet<Type>();
        var pending = new Stack<Type>(roots);
        while (pending.TryPop(out var type))
        {
            if (type.IsArray) pending.Push(type.GetElementType()!);
            foreach (var argument in type.GetGenericArguments()) pending.Push(argument);
            if (type.Assembly != typeof(ApiContractExtensions).Assembly || !found.Add(type)) continue;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) pending.Push(property.PropertyType);
        }
        return found;
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ApiContractValuesAttribute(params string[] values) : Attribute
{
    public IReadOnlyList<string> Values { get; } = values;
}
