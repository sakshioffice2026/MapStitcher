using MapStitcher.Business.Contracts;
using MapStitcher.Business.Services;
using MapStitcher.Database;
using MapStitcher.Repositories.Contracts;
using MapStitcher.Repositories.Repositories;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(
        connectionString,
        new MySqlServerVersion(new Version(8, 0, 21)),
        x => x.UseNetTopologySuite()
    ));

builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<ISurveySheetRepository, SurveySheetRepository>();


// 
builder.Services.AddScoped<ISheetArrangementOrchestrator, SheetArrangementOrchestrator>();
builder.Services.AddScoped<IIndexMapExtractionService, IndexMapExtractionService>();
builder.Services.AddScoped<ITopologyGridService, TopologyGridService>();
builder.Services.AddScoped<INeatlineExtractionService, NeatlineExtractionService>();
builder.Services.AddScoped<ISheetArrangementOrchestrator, SheetArrangementOrchestrator>();

builder.Services.AddScoped<
    ICadCoordinateInspectionService,
    CadCoordinateInspectionService>();


builder.Services.AddScoped<ICadSheetGridService,CadSheetGridService>();


var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();