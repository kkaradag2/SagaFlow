using Sample.Participants.ManagerApproval;
using Sample.Participants.ManagerApproval.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddSwaggerGen(options => options.OperationFilter<ApprovalDecisionExamples>());
builder.Services.AddManagerApproval(builder.Configuration);

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.Run();
