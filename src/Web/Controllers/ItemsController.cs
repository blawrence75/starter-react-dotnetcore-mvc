﻿using AutoMapper;
using Azure;
using StarterApp.Core.Entities;
using StarterApp.Infrastructure.Data;
using StarterApp.Infrastructure.Services;
using StarterApp.Infrastructure.Services.Models;
using StarterApp.Web.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Search.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MoreLinq;


namespace Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ItemsController : ControllerBase
    {
        private readonly ILogger<ItemsController> _logger;
        private readonly StarterAppDbContext _dbContext;
        private readonly IMapper _mapper;
        private readonly IDistributedCache _cache;
        private readonly ISearchService _searchService;
        private readonly IStorageService _blobService;
        private readonly int _cacheExpirationMins = 30;
        private readonly string _storageContainerName = "items";
        private readonly string _envName;
        private readonly string _searchIndexName;
        private readonly int _batchRange;

        public ItemsController(ILogger<ItemsController> logger, StarterAppDbContext dbContext, IMapper mapper,
                                 IDistributedCache cache, ISearchService searchService, IStorageService storageService,
                                 IOptions<AzureEnvironment> environment)
        {
            _logger = logger;
            _dbContext = dbContext;
            _mapper = mapper;
            _cache = cache;
            _searchService = searchService;
            _blobService = storageService;
            //The environment should be removed
            _envName = environment.Value.Name;
            _searchIndexName = _envName + "-items";
            _batchRange = 32000;
        }

        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetAsync()
        {
            var key = $"{_envName}:items";
            var Items = await _cache.GetStringAsync(key);
            if (Items == null)
            {
                var ItemsFrmDb = await _dbContext.Items
                                        .OrderByDescending(c => c.DateAdded)
                                        .ToListAsync();
                if (ItemsFrmDb is null)
                    return NotFound();
                else
                {
                    Items = JsonSerializer.Serialize(_mapper.Map<List<ItemDto>>(ItemsFrmDb));
                    var options = new DistributedCacheEntryOptions();
                    options.SetAbsoluteExpiration(DateTimeOffset.Now.AddMinutes(_cacheExpirationMins));
                    await _cache.SetStringAsync(key, Items, options);
                }
            }
            var ItemsOutDto = JsonSerializer.Deserialize<List<ItemDto>>(Items);
            return Ok(ItemsOutDto);
        }

        [HttpGet("{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetAsync(int Id)
        {
            var key = $"{_envName}:items:{Id}";
            var Item = await _cache.GetStringAsync(key);
            if (Item is null)
            {
                var ItemFrmDb = await _dbContext.Items.SingleOrDefaultAsync(c => c.ItemId == Id);

                if (ItemFrmDb is null)
                    return NotFound();
                else
                {

                    Item = JsonSerializer.Serialize(_mapper.Map<ItemDto>(ItemFrmDb));
                    var options = new DistributedCacheEntryOptions();
                    options.SetAbsoluteExpiration(DateTimeOffset.Now.AddMinutes(_cacheExpirationMins));
                    await _cache.SetStringAsync(key, Item, options);

                }
            }
            var ItemDtoOut = JsonSerializer.Deserialize<ItemDto>(Item);
            return Ok(ItemDtoOut);
        }

        [HttpGet("search")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<DocumentSearchResult<ItemSm>> SearchAsync([FromQuery] string searchText,
                                                                 [FromQuery] string[] searchFields,
                                                                 [FromQuery] string[] select)
        {
            return await _searchService.SearchAsync<ItemSm>(_searchIndexName, searchText, searchFields, select);
        }

        [HttpPost("search")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<DocumentSearchResult<ItemSm>> SearchAsync([FromQuery] string searchText,
                                                                        [FromBody] SearchParameters parameters)
        {
            return await _searchService.SearchAsync<ItemSm>(_searchIndexName, searchText, parameters);
        }


        [HttpPost]
        // [Authorize(Roles = "ItemManager")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Create(ItemDto ItemDto)
        {

            if (!ModelState.IsValid)
                return BadRequest();

            var Item = _mapper.Map<Item>(ItemDto);
            Item.DateAdded = DateTime.UtcNow;
            _dbContext.Add(Item);
            await _dbContext.SaveChangesAsync();

            if (!_searchService.CheckIndexExists(_searchIndexName))
                await _searchService.CreateIndexAsync<ItemSm>(_searchIndexName);
            await _searchService.UploadDocumentsAsync(_searchIndexName, new[] { _mapper.Map<ItemSm>(Item) });

            await _cache.RemoveAsync($"{_envName}:items");

            var ItemDtoOut = _mapper.Map<ItemDto>(Item);
            return Created(new Uri(Request.GetEncodedUrl()).ToString() + "/" + ItemDtoOut.ItemId, ItemDtoOut);
        }

        [HttpPut("{id}")]
        // [Authorize(Roles = "ItemManager")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Update(int Id, [FromBody] ItemDto ItemDto)
        {

            if (!ModelState.IsValid)
                return BadRequest();

            var ItemFrmDb = await _dbContext.Items.SingleOrDefaultAsync(c => c.ItemId == Id);

            if (ItemFrmDb is null)
                return NotFound();

            var Item = _mapper.Map(ItemDto, ItemFrmDb);
            _dbContext.Update(Item);
            await _dbContext.SaveChangesAsync();

            if (!_searchService.CheckIndexExists(_searchIndexName))
                await _searchService.CreateIndexAsync<ItemSm>(_searchIndexName);
            await _searchService.UploadDocumentsAsync(_searchIndexName, new[] { _mapper.Map<ItemSm>(Item) });

            await _cache.RemoveAsync($"{_envName}:items");
            await _cache.RemoveAsync($"{_envName}:items:{Id}");

            var ItemDtoOut = _mapper.Map<ItemDto>(Item);
            return Ok(ItemDtoOut);
        }


        [HttpDelete("{id}")]
        // [Authorize(Roles = "ItemManager")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(int Id)
        {

            var Item = await _dbContext.Items.SingleOrDefaultAsync(c => c.ItemId == Id);

            if (Item == null)
                return NotFound();
            else
            {
                var ItemDto = _mapper.Map<ItemDto>(Item);
                try
                {
                    _dbContext.Items.Remove(Item);
                    await _dbContext.SaveChangesAsync();

                    if (_searchService.CheckIndexExists(_searchIndexName))
                        await _searchService.DeleteDocumentsAsync(_searchIndexName, new[] { _mapper.Map<ItemSm>(Item) });

                    if (await _blobService.CheckBlobExists(_storageContainerName, Item.FileName))
                        await _blobService.DeleteAsync(_storageContainerName, Item.FileName);

                    await _cache.RemoveAsync($"{_envName}:items");
                    await _cache.RemoveAsync($"{_envName}:items:{Id}");

                    return Ok(ItemDto);
                }
                catch (Exception e)
                {
                    throw e;
                }
            }
        }

        [HttpGet("key")]
        // [Authorize(Roles = "ItemManager")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetContainerKey(int permissions = 1, int expireDurationInHours = 1)
        {
            try
            {
                var response = _blobService.GetContainerKey(_storageContainerName,
                                                        permissions, expireDurationInHours);
                return Ok(new { key = response });
            }
            catch (RequestFailedException e)
            {
                if (e.Status == StatusCodes.Status404NotFound)
                    return NotFound($"Container : {_storageContainerName} is not found");
                else throw e;
            }
        }

        [HttpPost("searchindex")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<DocumentIndexResult> IndexAsync()
        {

            await _searchService.DeleteIndexIfExistsAsync(_searchIndexName);
            await _searchService.CreateIndexAsync<ItemSm>(_searchIndexName);

            var Items = _dbContext.Items.Select(_mapper.Map<Item, ItemSm>);

            List<IndexingResult> results = new List<IndexingResult>();
            foreach (var batchItems in Items.Batch(_batchRange))
            {
                var uploadResponse = await _searchService.UploadDocumentsAsync(_searchIndexName, batchItems.ToArray());
                results.AddRange(uploadResponse.Results);
            }
            return new DocumentIndexResult(results);
        }

        [HttpDelete("searchindex")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteIndexAsync()
        {
            if (_searchService.CheckIndexExists(_searchIndexName))
            {
                await _searchService.DeleteIndexIfExistsAsync(_searchIndexName);
                return Ok();
            }
            else return NotFound();
        }

        // [HttpPost("searchindex/{id}")]
        // [ProducesResponseType(StatusCodes.Status200OK)]
        // public async Task<DocumentIndexResult> IndexAsync(int Id)
        // {
        //     if (!await _searchService.CheckIndexExists(_searchIndexName))
        //         await _searchService.CreateIndexAsync<ItemSm>(_searchIndexName);

        //     var Items = _dbContext.Items
        //             .Where(c => c.ItemId == Id)
        //             .Select(_mapper.Map<Item, ItemSm>);

        //     return await _searchService.UploadDocumentsAsync(_searchIndexName, Items.ToArray());
        // }

        // [HttpDelete("searchindex/{id}")]
        // [ProducesResponseType(StatusCodes.Status200OK)]
        // public async Task<IActionResult> DeleteIndexAsync(int Id)
        // {

        //     if (await _searchService.CheckIndexExists(_searchIndexName))
        //     {
        //         var items = _dbContext.Items
        //                 .Where(c => c.ItemId == Id)
        //                 .Select(_mapper.Map<Item, ItemSm>);

        //         List<IndexingResult> results = new List<IndexingResult>();
        //         foreach (var batchItems in items.Batch(_batchRange))
        //         {
        //             var deleteResponse = await _searchService.DeleteDocumentsAsync(_searchIndexName, batchItems.ToArray());
        //             results.AddRange(deleteResponse.Results);
        //         }

        //         return Ok();
        //     }
        //     return NotFound();
        // }

    }
}