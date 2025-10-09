using Alberto.ServiceDefaults;
using Alberto.SqlMigrator;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();
host.Run();