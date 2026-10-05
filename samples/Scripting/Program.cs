using Butterfly.Scripting;
using Butterfly.Scripting.AspNetCore;
using Butterfly.Scripting.CSharp;
using Butterfly.Scripting.Lua;
using Butterfly.Scripting.Lua.MoonSharp;
using Butterfly.Scripting.Lua.NLua;
using Test;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddScriptProblemDetails();
builder.Services.AddScoped<CustomSampleContext>();
builder.Services.AddCSharpScriptInvoker();
builder.Services.AddKeyedScriptInvoker("delegate", invoker => invoker
    .AddCSharp(csharp => csharp
        .WithDefaults()
        .WithType<CustomSampleContext>()
        .UseDelegateMode()
        .WithCompilationCache())
    .WithTimeout(TimeSpan.FromSeconds(5))
    .WithLogging()
    .WithTelemetry());
builder.Services.AddKeyedScriptInvoker("moonsharp", invoker => invoker
    .AddLua(lua => lua.UseMoonSharp())
    .WithTimeout(TimeSpan.FromSeconds(5)));
builder.Services.AddKeyedScriptInvoker("nlua", invoker => invoker
    .AddLua(lua => lua.UseNLua())
    .WithTimeout(TimeSpan.FromSeconds(5)));

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "v1");
        options.RoutePrefix = string.Empty;
    });

    app.MapScriptEndpoint("/invoke", ScriptingLanguage.CSharp)
        .WithName("InvokeScript")
        .WithSummary("Ejecuta un script C# con parámetros");

    app.MapScriptEndpoint("/invoke/delegate/{aaa}", ScriptingLanguage.CSharp, invokerKey: "delegate")
        .WithName("InvokeDelegateScript")
        .WithSummary("Ejecuta un delegado C# cuyos parámetros se resuelven por nombre o por inyección de dependencias");

    app.MapScriptEndpoint("/invoke/lua/moonsharp", ScriptingLanguage.Lua, invokerKey: "moonsharp")
        .WithName("InvokeMoonSharpScript")
        .WithSummary("Ejecuta un script Lua con MoonSharp; los parámetros se exponen como variables globales");

    app.MapScriptEndpoint("/invoke/lua/nlua", ScriptingLanguage.Lua, invokerKey: "nlua")
        .WithName("InvokeNLuaScript")
        .WithSummary("Ejecuta un script Lua con NLua (Lua 5.4 nativo); los parámetros se exponen como variables globales");
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
await app.RunAsync();
