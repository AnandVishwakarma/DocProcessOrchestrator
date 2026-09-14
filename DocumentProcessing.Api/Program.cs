using DocumentProcessing.Agents;
using DocumentProcessing.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDocumentProcessingInfrastructure(builder.Configuration);
builder.Services.AddDocumentProcessingAgents();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Serve wwwroot/index.html at "/" before MVC routing so the workflow console loads directly.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.Run();
