using System.Net.Http.Headers;
using Blockfrost.Api.Extensions;
using Blockfrost.Api.Models;
using Blockfrost.Api.Services;
using Microsoft.AspNetCore.Mvc;
using HOSKYSWAP.Data;
using Microsoft.EntityFrameworkCore;
using HOSKYSWAP.Common;

var builder = WebApplication.CreateBuilder(args);
builder.Host.ConfigureLogging(logging =>
{
	logging.ClearProviders();
	logging.AddConsole();
});

builder.Services.AddCors(options =>
{
	options.AddPolicy(name: "AllowAll",
		builder =>
		{
			builder
			.AllowAnyOrigin()
			.AllowAnyMethod()
			.AllowAnyHeader();
		}
	);
});

string connectionString = builder.Configuration.GetConnectionString("HOSKYSWAPDB");
var blockfrostProjectID = builder.Configuration["BlockfrostProjectID"];
var blockfrostAPI = builder.Configuration["BlockfrostAPI"];
var cardanoNetwork = builder.Configuration["CardanoNetwork"];

builder.Services.AddDbContext<HoskyDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddBlockfrost(cardanoNetwork, blockfrostProjectID);
var app = builder.Build();
app.UseCors("AllowAll");

var _logger = app.Logger;

app.MapGet("/parameters", async (IEpochsService bfEpochService) => await bfEpochService.GetLatestParametersAsync());

app.MapGet("/blocks/latest", async (IBlocksService bfBlockService) => await bfBlockService.GetLatestAsync());

app.MapGet("/txs/{hash}", async ([FromRoute] string hash, ITransactionsService bfTxService) => await bfTxService.GetAsync(hash));

app.MapGet("/order/last/execute", async (HoskyDbContext dbContext) =>
{
	if (dbContext.Orders is not null)
	{
		return await dbContext.Orders.Where(o => o.Status == Status.Filled).OrderByDescending(o => o.UpdatedAt).FirstOrDefaultAsync();
	}
	else
		throw new Exception("Server error occured. Please try again.");
});


app.MapGet("/order/{address}", async ([FromRoute] string address, HoskyDbContext dbContext) =>
{
	if (dbContext.Orders is not null)
	{
		return await dbContext.Orders.Where(o => o.OwnerAddress == address && (o.Status == Status.Filled || o.Status == Status.Cancelled)).OrderByDescending(o => o.UpdatedAt).Take<Order>(100).ToListAsync<Order>();
	}
	else
		throw new Exception("Server error occured. Please try again.");
});

app.MapGet("/order/{address}/open", async ([FromRoute] string address, HoskyDbContext dbContext) =>
{
	if (dbContext.Orders is not null)
	{
		return await dbContext.Orders.Where(o => o.OwnerAddress == address && (o.Status == Status.Open || o.Status == Status.Cancelling)).OrderByDescending(o => o.CreatedAt).ToListAsync<Order>();
	}
	else
		throw new Exception("Server error occured. Please try again.");
});

app.MapGet("/order/open/buy", async (HoskyDbContext dbContext) =>
{
	if (dbContext.Orders is not null)
	{
		return await dbContext.Orders.Where(o => o.Action.ToLower() == "buy" && o.Status == Status.Open).OrderByDescending(o => o.Rate).Take<Order>(100).ToListAsync<Order>();
	}
	else
		throw new Exception("Server error occured. Please try again.");
});

app.MapGet("/order/open/sell", async (HoskyDbContext dbContext) =>
{
	if (dbContext.Orders is not null)
	{
		return await dbContext.Orders.Where(o => o.Action.ToLower() == "sell" && o.Status == Status.Open).OrderBy(o => o.Rate).Take<Order>(100).ToListAsync<Order>();
	}
	else
		throw new Exception("Server error occured. Please try again.");
});

app.MapGet("/order/open/ratio", async (HoskyDbContext dbContext) =>
{
	if (dbContext.Orders is not null)
	{
		var filedSellOrders = await dbContext.Orders.Where(o => o.Action.ToLower() == "sell" && o.Status == Status.Open).ToListAsync<Order>();
		var filledBuyOrders = await dbContext.Orders.Where(o => o.Action.ToLower() == "buy" && o.Status == Status.Open).ToListAsync<Order>();

		ulong totalOpenSell = 0;
		ulong totalOpenBuy = 0;

		filledBuyOrders.ForEach(e => totalOpenBuy += e.Total);
		filedSellOrders.ForEach(e => totalOpenSell += (ulong)((e.Total * e.Rate) * 1000000));

		var total = totalOpenSell + totalOpenBuy;
		decimal sellRatio = 0;
		decimal buyRatio = 0;

		if (totalOpenBuy > 0) buyRatio = (((decimal)totalOpenBuy / (decimal)total) * 100);
		if (totalOpenSell > 0) sellRatio = (((decimal)totalOpenSell / (decimal)total) * 100);

		var result = new OpenOrderRatio
		{
			Sell = sellRatio,
			Buy = buyRatio
		};
		return result;
	}
	else
		throw new Exception("Server error occured. Please try again.");
});

app.MapGet("/market/daily/volume", async (HoskyDbContext dbContext) =>
{
	if (dbContext.Orders is not null)
	{
		var yesterday = DateTime.UtcNow.AddDays(-1);
		var filledOrders = await dbContext.Orders
			.Where(o => o.Status == Status.Filled && o.UpdatedAt >= yesterday && o.Action.ToLower() == "buy")
			.ToListAsync();

		var totalAdaVolume = 0UL;
		filledOrders.ForEach(e => totalAdaVolume += e.Total);

		var result = new Volume
		{
			Amount = (totalAdaVolume * 2) / 1000000,
			Currency = "$ADA"
		};
		return result;
	}
	else
		throw new Exception("Server error occured. Please try again.");
});

app.MapGet("/order/total/rugpulled", async (HoskyDbContext dbContext) =>
{
	if (dbContext.Orders is not null)
	{
		var filledOrders = await dbContext.Orders.Where(o => o.Status == Status.Filled).ToListAsync<Order>();
		return filledOrders.Count * 0.694200m;
	}
	else
		throw new Exception("Server error occured. Please try again.");
});

app.MapGet("/order/history", async (HoskyDbContext dbContext) => {
	if (dbContext.Orders is not null)
	{
		return await dbContext.Orders.Where(e => e.Status == Status.Filled).OrderByDescending(e => e.UpdatedAt).Take(100).ToListAsync<Order>();
	}
	else
		throw new Exception("Server error occured. Please try again.");
});

app.MapPost("/tx/submit", SumbitTx);

_logger.LogInformation($"API Server running at: {DateTimeOffset.Now}");
app.Run();

#region DELEGATES

async Task SumbitTx(HttpContext ctx)
{
	try
	{
		using var reader = new StreamReader(ctx.Request.Body);
		using var memStream = new MemoryStream();
		using var httpClient = new HttpClient();
		await reader.BaseStream.CopyToAsync(memStream);

		httpClient.DefaultRequestHeaders.Add("project_id", blockfrostProjectID);
		var byteContent = new ByteArrayContent(memStream.ToArray());
		byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/cbor");
		var txResponse = await httpClient.PostAsync($"{blockfrostAPI}/tx/submit", byteContent);
		var txId = await txResponse.Content.ReadAsStringAsync();
		txId = txId.Replace("\"", string.Empty);
		Console.WriteLine(txId);

		if (txId.Length != 64) throw new Exception(txId);

		await ctx.Response.WriteAsJsonAsync(new { status = 200, result = txId });
	}
	catch (Exception e)
	{
		_logger.LogError($"Error in fetching transaction: {e}");
		await ctx.Response.WriteAsJsonAsync(new { error = true, message = e });
	}
}


#endregion