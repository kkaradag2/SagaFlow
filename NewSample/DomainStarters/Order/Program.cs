using Sample.DomainStarters.Order.Data;
using Sample.DomainStarters.Order.Observability;
using Sample.DomainStarters.Order.Orchestration;

var builder = WebApplication.CreateBuilder(args);
builder.AddOrderLogging();
builder.Services.AddControllers();
builder.Services.AddSwaggerGen();
builder.Services.AddOrderServices(builder.Configuration);
builder.Services.AddOrderSagaFlow(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.Run();
