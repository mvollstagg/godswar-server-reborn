using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    Environment.GetEnvironmentVariable("GODSWAR_POSTGRES_CONNECTION_STRING")
    ?? "Host=127.0.0.1;Port=5432;Database=godswar;Username=godswar;Password=godswar_dev_password;Pooling=true";

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ok" });
});

app.MapGet("/api/characters", async (string? q, NpgsqlDataSource db, CancellationToken ct) =>
{
    var search = string.IsNullOrWhiteSpace(q) ? "%" : $"%{q.Trim()}%";
    await using var cmd = db.CreateCommand("""
        SELECT c.id, c.account_id, a.username, c.name, c.profession, c.camp,
               c.fighter_job_lv, c."Money", c."Stone", c."SkillPoint",
               c."Map", c."Pos_X", c."Pos_Z", c."GM"
        FROM public.character_base c
        JOIN public.accounts a ON a.id = c.account_id
        WHERE c.name ILIKE @q OR a.username ILIKE @q
        ORDER BY c.id
        LIMIT 100;
        """);
    cmd.Parameters.AddWithValue("q", search);

    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        list.Add(new
        {
            id = reader.GetInt32(0),
            accountId = reader.GetInt32(1),
            username = reader.GetString(2),
            name = reader.GetString(3),
            profession = reader.GetInt16(4),
            camp = reader.GetInt16(5),
            level = reader.GetInt32(6),
            money = reader.GetInt32(7),
            stone = reader.GetInt32(8),
            skillPoint = reader.GetInt32(9),
            map = reader.GetInt16(10),
            posX = reader.GetFloat(11),
            posZ = reader.GetFloat(12),
            gm = reader.GetInt16(13)
        });
    }
    return Results.Ok(list);
});

app.MapPost("/api/characters/{id:int}", async (int id, UpdateCharacterRequest req, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("""
        UPDATE public.character_base
        SET fighter_job_lv = @level,
            "Money" = @money,
            "Stone" = @stone,
            "SkillPoint" = @skillPoint,
            "GM" = @gm,
            "Map" = @map,
            "Pos_X" = @posX,
            "Pos_Z" = @posZ
        WHERE id = @id;
        """);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("level", Math.Clamp(req.Level, 1, 200));
    cmd.Parameters.AddWithValue("money", Math.Max(0, req.Money));
    cmd.Parameters.AddWithValue("stone", Math.Max(0, req.Stone));
    cmd.Parameters.AddWithValue("skillPoint", Math.Max(0, req.SkillPoint));
    cmd.Parameters.AddWithValue("gm", req.Gm);
    cmd.Parameters.AddWithValue("map", req.Map);
    cmd.Parameters.AddWithValue("posX", req.PosX);
    cmd.Parameters.AddWithValue("posZ", req.PosZ);

    var rows = await cmd.ExecuteNonQueryAsync(ct);
    return rows == 0 ? Results.NotFound() : Results.Ok(new { updated = true });
});

app.MapGet("/api/characters/{id:int}/items", async (int id, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("""
        SELECT ci.id, ci.item_location, ci.slot_index, ci.prop_id, ci.stack,
               COALESCE(it.display_name, 'Item #' || ci.prop_id) AS display_name,
               COALESCE(it.kind, '') AS kind
        FROM public.character_items ci
        LEFT JOIN public.item_templates it ON it.id = ci.prop_id
        WHERE ci.user_id = @id
        ORDER BY ci.item_location, ci.slot_index;
        """);
    cmd.Parameters.AddWithValue("id", id);

    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        list.Add(new
        {
            id = reader.GetInt64(0),
            location = reader.GetInt16(1),
            slot = reader.GetInt16(2),
            itemId = reader.GetInt32(3),
            stack = reader.GetInt32(4),
            name = reader.GetString(5),
            kind = reader.GetString(6)
        });
    }
    return Results.Ok(list);
});

app.MapPost("/api/characters/{id:int}/items", async (int id, GrantItemRequest req, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var conn = await db.OpenConnectionAsync(ct);
    await using var usedCmd = new NpgsqlCommand(
        "SELECT slot_index FROM public.character_items WHERE user_id = @id AND item_location = 1;", conn);
    usedCmd.Parameters.AddWithValue("id", id);
    var used = new HashSet<int>();
    await using (var r = await usedCmd.ExecuteReaderAsync(ct))
    {
        while (await r.ReadAsync(ct)) used.Add(r.GetInt16(0));
    }

    var freeSlot = -1;
    for (var s = 0; s < 24; s++)
    {
        if (!used.Contains(s)) { freeSlot = s; break; }
    }
    if (freeSlot < 0)
    {
        return Results.BadRequest(new { error = "Character bag (slots 0..23) is full." });
    }

    await using var insertCmd = new NpgsqlCommand("""
        INSERT INTO public.character_items (user_id, item_location, slot_index, prop_id, stack)
        VALUES (@userId, 1, @slot, @itemId, @stack)
        RETURNING id;
        """, conn);
    insertCmd.Parameters.AddWithValue("userId", id);
    insertCmd.Parameters.AddWithValue("slot", (short)freeSlot);
    insertCmd.Parameters.AddWithValue("itemId", req.ItemId);
    insertCmd.Parameters.AddWithValue("stack", Math.Clamp(req.Stack, 1, 999));

    var insertedId = await insertCmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { id = insertedId, slot = freeSlot });
});

app.MapDelete("/api/characters/{id:int}/items/{itemInstanceId:long}", async (int id, long itemInstanceId, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand(
        "DELETE FROM public.character_items WHERE id = @itemId AND user_id = @userId;");
    cmd.Parameters.AddWithValue("itemId", itemInstanceId);
    cmd.Parameters.AddWithValue("userId", id);
    var rows = await cmd.ExecuteNonQueryAsync(ct);
    return rows == 0 ? Results.NotFound() : Results.Ok(new { deleted = true });
});

app.MapGet("/api/characters/{id:int}/pets", async (int id, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("""
        SELECT id, name, species_id, level, aptitude, rank, activity_state, is_summoned, is_carried
        FROM public.character_pets
        WHERE user_id = @id
        ORDER BY id;
        """);
    cmd.Parameters.AddWithValue("id", id);

    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        list.Add(new
        {
            id = reader.GetInt64(0),
            name = reader.GetString(1),
            speciesId = reader.GetInt16(2),
            level = reader.GetInt16(3),
            aptitude = reader.GetInt16(4),
            rank = reader.GetDecimal(5),
            activityState = reader.GetString(6),
            isSummoned = reader.GetBoolean(7),
            isCarried = reader.GetBoolean(8)
        });
    }
    return Results.Ok(list);
});

app.MapGet("/api/items", async (string? q, NpgsqlDataSource db, CancellationToken ct) =>
{
    var trimmed = q?.Trim() ?? "";
    var isNumeric = int.TryParse(trimmed, out var exactId);
    await using var cmd = db.CreateCommand("""
        SELECT id, kind, display_name, min_level, equipment_slot
        FROM public.item_templates
        WHERE (@hasId AND id = @exactId)
           OR (@q = '' OR display_name ILIKE @pattern OR kind ILIKE @pattern)
        ORDER BY CASE WHEN @hasId AND id = @exactId THEN 0 ELSE 1 END, id
        LIMIT 100;
        """);
    cmd.Parameters.AddWithValue("hasId", isNumeric);
    cmd.Parameters.AddWithValue("exactId", exactId);
    cmd.Parameters.AddWithValue("q", trimmed);
    cmd.Parameters.AddWithValue("pattern", $"%{trimmed}%");

    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        list.Add(new
        {
            id = reader.GetInt32(0),
            kind = reader.GetString(1),
            name = reader.GetString(2),
            minLevel = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
            slot = reader.GetInt32(4)
        });
    }
    return Results.Ok(list);
});

app.MapGet("/api/realms", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand(
        "SELECT id, name, enabled FROM public.server ORDER BY id;");
    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        list.Add(new
        {
            id = reader.GetInt32(0),
            name = reader.GetString(1),
            enabled = reader.GetBoolean(2)
        });
    }
    return Results.Ok(list);
});

app.MapPost("/api/realms/{id:int}", async (int id, UpdateRealmRequest req, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand(
        "UPDATE public.server SET name = @name, enabled = @enabled WHERE id = @id;");
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("name", req.Name.Trim());
    cmd.Parameters.AddWithValue("enabled", req.Enabled);
    var rows = await cmd.ExecuteNonQueryAsync(ct);
    return rows == 0 ? Results.NotFound() : Results.Ok(new { updated = true });
});

app.MapGet("/api/loot", async (string? q, NpgsqlDataSource db, CancellationToken ct) =>
{
    var search = string.IsNullOrWhiteSpace(q) ? "%" : $"%{q.Trim()}%";
    await using var cmd = db.CreateCommand("""
        SELECT t.template_key, t.maximum_drops, t.enabled, COUNT(r.loot_index)::int AS rule_count
        FROM public.monster_loot_tables t
        LEFT JOIN public.monster_loot_rules r ON r.template_key = t.template_key
        WHERE t.template_key ILIKE @q
        GROUP BY t.template_key, t.maximum_drops, t.enabled
        ORDER BY t.template_key
        LIMIT 100;
        """);
    cmd.Parameters.AddWithValue("q", search);

    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        list.Add(new
        {
            templateKey = reader.GetString(0),
            maximumDrops = reader.GetInt16(1),
            enabled = reader.GetBoolean(2),
            ruleCount = reader.GetInt32(3)
        });
    }
    return Results.Ok(list);
});

app.MapGet("/api/loot/{templateKey}", async (string templateKey, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("""
        SELECT r.loot_index, r.item_id, COALESCE(it.display_name, 'Item #' || r.item_id),
               r.chance_basis_points, r.minimum_quantity, r.maximum_quantity, r.enabled
        FROM public.monster_loot_rules r
        LEFT JOIN public.item_templates it ON it.id = r.item_id
        WHERE r.template_key = @key
        ORDER BY r.loot_index;
        """);
    cmd.Parameters.AddWithValue("key", templateKey);

    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        list.Add(new
        {
            lootIndex = reader.GetInt16(0),
            itemId = reader.GetInt32(1),
            itemName = reader.GetString(2),
            chanceBasisPoints = reader.GetInt32(3),
            minQty = reader.GetInt16(4),
            maxQty = reader.GetInt16(5),
            enabled = reader.GetBoolean(6)
        });
    }
    return Results.Ok(list);
});

app.MapGet("/api/quests", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("""
        SELECT quest_id, experience, talent_points, silver, gold
        FROM public.quest_reward_values
        ORDER BY quest_id
        LIMIT 100;
        """);
    var list = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        list.Add(new
        {
            questId = reader.GetInt32(0),
            experience = reader.GetInt32(1),
            talentPoints = reader.GetInt32(2),
            silver = reader.GetInt32(3),
            gold = reader.GetInt32(4)
        });
    }
    return Results.Ok(list);
});

app.MapPost("/api/quests/{questId:int}", async (int questId, UpdateQuestRequest req, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("""
        INSERT INTO public.quest_reward_values (quest_id, experience, talent_points, silver, gold)
        VALUES (@id, @exp, @tp, @silver, @gold)
        ON CONFLICT (quest_id) DO UPDATE SET
            experience = EXCLUDED.experience,
            talent_points = EXCLUDED.talent_points,
            silver = EXCLUDED.silver,
            gold = EXCLUDED.gold;
        """);
    cmd.Parameters.AddWithValue("id", questId);
    cmd.Parameters.AddWithValue("exp", Math.Max(0, req.Experience));
    cmd.Parameters.AddWithValue("tp", Math.Max(0, req.TalentPoints));
    cmd.Parameters.AddWithValue("silver", Math.Max(0, req.Silver));
    cmd.Parameters.AddWithValue("gold", Math.Max(0, req.Gold));
    await cmd.ExecuteNonQueryAsync(ct);
    return Results.Ok(new { updated = true });
});

app.Run();

internal sealed record UpdateCharacterRequest(
    int Level,
    int Money,
    int Stone,
    int SkillPoint,
    short Gm,
    short Map,
    float PosX,
    float PosZ);

internal sealed record GrantItemRequest(int ItemId, int Stack = 1);
internal sealed record UpdateRealmRequest(string Name, bool Enabled);
internal sealed record UpdateQuestRequest(int Experience, int TalentPoints, int Silver, int Gold);
