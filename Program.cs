using Microsoft.EntityFrameworkCore;
using OrderVerificationAPI;
using OrderVerificationAPI.Interfaces;
using OrderVerificationAPI.Models;
using OrderVerificationAPI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register HttpClient to resolve IHttpClientFactory
builder.Services.AddHttpClient();

// Configure DbContext
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Configure UltraMsgSettings
builder.Services.Configure<UltraMsgSettings>(builder.Configuration.GetSection("UltraMsgSettings"));

// Configure custom services (OrderService)
builder.Services.AddScoped<IOrderService, OrderService>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    //var bodyAsText = await new StreamReader(context.Request.Body).ReadToEndAsync();
    //Console.WriteLine($"Raw request body: {bodyAsText}");
    //context.Request.Body.Position = 0; // Reset the request body stream position
    await next();
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
