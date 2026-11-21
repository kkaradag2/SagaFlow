using Sample.Participants.Warehouse;
using Sample.Participants.Warehouse.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddSwaggerGen(options => options.OperationFilter<InspectionDecisionExamples>());
builder.Services.AddWarehouse(builder.Configuration);

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.Run();
