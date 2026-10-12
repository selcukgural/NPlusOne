using Microsoft.AspNetCore.Mvc;

namespace NPlusOne.AspNetCore.Tests;

[ApiController]
[Route("mvc")]
public sealed class ShopController(ShopContext db) : ControllerBase
{
    [HttpGet("loop")]
    [NPlusOneAssert]
    public async Task<int> Loop() => await db.CountOrdersOneByOneAsync();

    [HttpGet("clean")]
    [NPlusOneAssert]
    public async Task<int> Clean() => await db.CountOrdersAtOnceAsync();

    [HttpGet("unchecked-loop")]
    public async Task<int> UncheckedLoop() => await db.CountOrdersOneByOneAsync();
}
