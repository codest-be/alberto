using Alberto.Example.Modules.Orders;
using Alberto.Example.Modules.Payments;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddOrdersModule(builder.Configuration)
    .AddPaymentsModule(builder.Configuration)
    .AddOpenApi();

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "My API V1");
        o.RoutePrefix = string.Empty;
    });
}

app.MapDefaultEndpoints();

app.MapOrdersModule();
app.MapPaymentsModule();

app.Run();