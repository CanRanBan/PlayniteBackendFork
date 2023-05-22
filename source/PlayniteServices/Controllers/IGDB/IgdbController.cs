﻿using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Playnite.Common;
using Playnite.SDK;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace PlayniteServices.Controllers.IGDB;

[Route("igdb")]
public class IgdbController : Controller
{
    private static readonly ILogger logger = LogManager.GetLogger();
    private readonly IgdbApi igdbApi;

    private static readonly List<GameCategoryEnum> defaultSearchCategories = new()
    {
        GameCategoryEnum.MAIN_GAME,
        GameCategoryEnum.REMAKE,
        GameCategoryEnum.REMASTER,
        GameCategoryEnum.STANDALONE_EXPANSION
    };

    private static readonly Dictionary<Guid, ExternalGameCategoryEnum> libraryIdCategories = new()
    {
        [new Guid("CB91DFC9-B977-43BF-8E70-55F46E410FAB")] = ExternalGameCategoryEnum.EXTERNALGAME_STEAM,
        [new Guid("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E")] = ExternalGameCategoryEnum.EXTERNALGAME_GOG,
        [new Guid("00000002-DBD1-46C6-B5D0-B1BA559D10E4")] = ExternalGameCategoryEnum.EXTERNALGAME_EPIC_GAME_STORE,
        [new Guid("00000001-EBB2-4EEC-ABCB-7C89937A42BB")] = ExternalGameCategoryEnum.EXTERNALGAME_ITCH_IO
    };

    private static readonly TextSearchOptions gameSearchOptons = new()
    {
        CaseSensitive = false,
        DiacriticSensitive = false
    };

    public IgdbController(IgdbApi igdbApi)
    {
        this.igdbApi = igdbApi;
    }

    [HttpGet("game/{gameId}")]
    public async Task<ResponseBase> GetGame(ulong gameId)
    {
        if (gameId == 0)
        {
            return new ErrorResponse("No ID specified.");
        }

        var game = await igdbApi.Games.GetItem(gameId);
        if (game != null)
        {
            return new DataResponse<Game>(game);
        }

        return new ErrorResponse("Game not found.");
    }

    [HttpPost("search")]
    public async Task<ResponseBase> SearchGames([FromBody] SearchRequest? searchRequest)
    {
        if (searchRequest is null)
        {
            return new ErrorResponse("Missing search data.");
        }

        if (searchRequest.SearchTerm.IsNullOrWhiteSpace())
        {
            return new ErrorResponse("No search term");
        }

        var games = await SearchGame(searchRequest.SearchTerm, true);
        return new DataResponse<List<Game>>(games.Select(a => a.Game).ToList());
    }

    [HttpPost("metadata")]
    public async Task<ResponseBase> GetMetadata([FromBody] MetadataRequest? metadataRequest)
    {
        if (metadataRequest is null)
        {
            return new ErrorResponse("Missing metadata data.");
        }

        if (metadataRequest.LibraryId != Guid.Empty &&
            !metadataRequest.GameId.IsNullOrWhiteSpace() &&
            libraryIdCategories.TryGetValue(metadataRequest.LibraryId, out var externalCategory))
        {
            var filter = Builders<ExternalGame>.Filter;
            var externalGame = await igdbApi.ExternalGames.collection.
                Find(filter.Eq(a => a.uid, metadataRequest.GameId) & filter.Eq(a => a.category, externalCategory)).
                FirstOrDefaultAsync();
            if (externalGame != null)
            {
                return new DataResponse<Game>(await igdbApi.Games.GetItem(externalGame.game));
            }
        }

        var match = await TryMatchGame(metadataRequest);
        if (match == null)
        {
            return new DataResponse<Game>(default);
        }
        else
        {
            return new DataResponse<Game>(match);
        }
    }

    private async Task<List<TextSearchResult>> SearchGameByName(string searchTerm)
    {
        var filter = Builders<Game>.Filter;
        var catFilter = filter.In(a => a.category, defaultSearchCategories);
        var nameFilter = filter.Text(searchTerm, gameSearchOptons);
        return (await igdbApi.Games.collection.
            Find(catFilter & nameFilter).
            Project<Game>(Builders<Game>.Projection.MetaTextScore("textScore")).
            Sort(Builders<Game>.Sort.MetaTextScore("textScore")).                
            Limit(30).
            ToListAsync()).
                Select(a => new TextSearchResult(a.textScore, a.name!, a)).ToList();
    }

    private async Task<List<TextSearchResult>> SearchGameByAlternativeNames(string searchTerm)
    {
        var searchRes = await igdbApi.AlternativeNames.collection.
            Find(Builders<AlternativeName>.Filter.Text(searchTerm, gameSearchOptons)).
            Project<AlternativeName>(Builders<AlternativeName>.Projection.MetaTextScore("textScore")).
            Sort(Builders<AlternativeName>.Sort.MetaTextScore("textScore")).
            Limit(30).
            ToListAsync();
        var res = new List<TextSearchResult>(30);
        foreach (var item in searchRes)
        {
            await item.expand_game(igdbApi);
            if (item.game_expanded != null)
            {
                res.Add(new TextSearchResult(item.textScore, item.name!, item.game_expanded));
            }
        }

        return res;
    }

    private async Task<List<TextSearchResult>> SearchGame(string searchTerm, bool removeDuplicates)
    {
        var nameResults = await SearchGameByName(searchTerm);
        var altResults = await SearchGameByAlternativeNames(searchTerm);
        var res = new List<TextSearchResult>(60);
        res.AddRange(nameResults);
        res.AddRange(altResults);
        res.Sort((a, b) => a.TextScore.CompareTo(b.TextScore) * -1);
        if (removeDuplicates)
        {
            res = res.DistinctBy(a => a.Game.id).ToList();
        }

        return res;
    }

    private async Task<Game?> TryMatchGame(MetadataRequest metadataRequest)
    {
        if (metadataRequest.Name.IsNullOrWhiteSpace())
        {
            return null;
        }

        var name = SanitizeName(metadataRequest.Name);

        var results = await SearchGame(name, false);
        results.ForEach(a => a.Name = SanitizeName(a.Name));

        // Direct comparison
        var matchedGame = TryMatchGames(metadataRequest, name, results);
        if (matchedGame != null)
        {
            return matchedGame;
        }

        // Try replacing roman numerals: 3 => III
        var testName = Regex.Replace(name, @"\d+", ReplaceNumsForRomans);
        matchedGame = TryMatchGames(metadataRequest, testName, results);
        if (matchedGame != null)
        {
            return matchedGame;
        }

        // Try adding The
        testName = "The " + name;
        matchedGame = TryMatchGames(metadataRequest, testName, results);
        if (matchedGame != null)
        {
            return matchedGame;
        }

        // Try chaning & / and
        testName = Regex.Replace(name, @"\s+and\s+", " & ");
        matchedGame = TryMatchGames(metadataRequest, testName, results);
        if (matchedGame != null)
        {
            return matchedGame;
        }

        // Try removing apostrophes
        var resCopy = results.GetCopy();
        resCopy.ForEach(a => a.Name = a.Name!.Replace("'", "", StringComparison.Ordinal));
        matchedGame = TryMatchGames(metadataRequest, name, resCopy);
        if (matchedGame != null)
        {
            return matchedGame;
        }

        // Try removing all ":" and "-"
        testName = Regex.Replace(name, @"\s*(:|-)\s*", " ");
        resCopy = results.GetCopy();
        resCopy.ForEach(a => a.Name = Regex.Replace(a.Name!, @"\s*(:|-)\s*", " "));
        matchedGame = TryMatchGames(metadataRequest, testName, resCopy);
        if (matchedGame != null)
        {
            return matchedGame;
        }

        // Try without subtitle
        var testResult = results.FirstOrDefault(a =>
        {
            if (!string.IsNullOrEmpty(a.Name) && a.Name.Contains(':', StringComparison.InvariantCultureIgnoreCase))
            {
                return string.Equals(name, a.Name.Split(':')[0], StringComparison.InvariantCultureIgnoreCase);
            }

            return false;
        });

        if (testResult != null)
        {
            return testResult.Game;
        }

        return null;
    }

    private Game? TryMatchGames(MetadataRequest metadataRequest, string matchName, List<TextSearchResult> list)
    {
        var res = list.Where(a => string.Equals(matchName, a.Name, StringComparison.InvariantCultureIgnoreCase)).ToList();
        if (res.Count == 0)
        {
            return null;
        }

        if (res.Count == 1)
        {
            return res[0].Game;
        }
        
        if (res.Count > 1)
        {
            if (metadataRequest.ReleaseYear > 0)
            {
                var game = res.FirstOrDefault(a => a.Game.first_release_date.ToDateFromUnixSeconds().Year == metadataRequest.ReleaseYear);
                if (game != null)
                {
                    return game.Game;
                }
            }
            else
            {
                // If multiple matches are found and we don't have release date then prioritize older game
                if (res.All(a => a.Game.first_release_date == 0))
                {
                    return res[0].Game;
                }
                else
                {
                    var game = res.OrderBy(a => a.Game.first_release_date).FirstOrDefault(a => a.Game.first_release_date > 0);
                    if (game == null)
                    {
                        return res[0].Game;
                    }
                    else
                    {
                        return game.Game;
                    }
                }
            }
        }

        return null;
    }

    private static string SanitizeName(string name)
    {
        if (name.IsNullOrWhiteSpace())
        {
            return string.Empty;
        }

        var match = Regex.Match(name, @"(.+),\s*(the|a|an|der|das|die)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            name = match.Groups[2].Value + " " + match.Groups[1].Value;
        }

        name = Regex.Replace(name, @"\[.+?\]|\(.+?\)|\{.+?\}", string.Empty);
        name = name.RemoveTrademarks();
        name = name.Replace('_', ' ');
        name = name.Replace('.', ' ');
        name = name.Replace('’', '\'');
        name = Regex.Replace(name, @"\s+", " ");
        name = name.Replace(@"\", string.Empty, StringComparison.Ordinal);
        return name.Trim();
    }

    private string ReplaceNumsForRomans(Match m)
    {
        if (int.TryParse(m.Value, out var intVal))
        {
            return Roman.To(intVal);
        }
        else
        {
            return m.Value;
        }
    }
}
