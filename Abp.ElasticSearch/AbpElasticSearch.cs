﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Abp.Application.Services.Dto;
using Abp.ElasticSearch.Configuration;
using Elasticsearch.Net;
using Nest;

namespace Abp.ElasticSearch
{
    /// <summary>
    /// AbpElasticSearchPlug
    /// </summary>
    public class AbpElasticSearch : IElasticsearch
    {
        public IElasticClient EsClient { get; set; }

        public AbpElasticSearch(IElasticSearchConfigration elasticSearchConfigration)
        {
            _elasticSearchConfigration = elasticSearchConfigration;
            EsClient = GetClient();
        }


        private readonly IElasticSearchConfigration _elasticSearchConfigration;
        /// <summary>
        /// GetesClient
        /// </summary>
        /// <returns></returns>
        private ElasticClient GetClient()
        {
            var str = _elasticSearchConfigration.ConnectionString;
            var strs = str.Split('|');
            var nodes = strs.Select(s => new Uri(s)).ToList();
            var connectionPool = new SniffingConnectionPool(nodes);
            var connectionString = new ConnectionSettings(connectionPool);
            connectionString.BasicAuthentication(_elasticSearchConfigration.AuthUserName, _elasticSearchConfigration.AuthPassWord);

            return new ElasticClient(connectionString);
        }

        /// <summary>
        /// CreateEsIndex Not Mapping
        /// Auto Set Alias alias is Input IndexName
        /// </summary>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public virtual async Task CrateIndexAsync(string indexName)
        {
            var exis = await EsClient.IndexExistsAsync(indexName);
            if (exis.Exists)
                return;
            var newName = indexName + DateTime.Now.Ticks;
            var result = await EsClient
                .CreateIndexAsync(newName,
                    ss =>
                        ss.Index(newName)
                            .Settings(
                                o => o.NumberOfShards(1).NumberOfReplicas(1).Setting("max_result_window", int.MaxValue)));
            if (result.Acknowledged)
            {
                await EsClient.AliasAsync(al => al.Add(add => add.Index(newName).Alias(indexName)));
                return;
            }
            throw new ElasticSearchException($"Create Index {indexName} failed :" + result.ServerError.Error.Reason);
        }

        /// <summary>
        /// CreateEsIndex auto Mapping T Property
        /// Auto Set Alias alias is Input IndexName
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TKey"></typeparam>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public virtual async Task CreateIndexAsync<T, TKey>(string indexName) where T : EntityDto<TKey>
        {
            var exis = await EsClient.IndexExistsAsync(indexName);

            if (exis.Exists)
                return;
            var newName = indexName + DateTime.Now.Ticks;
            var result = await EsClient
                .CreateIndexAsync(newName,
                    ss =>
                        ss.Index(newName)
                            .Settings(
                                o => o.NumberOfShards(1).NumberOfReplicas(1).Setting("max_result_window", int.MaxValue))
                            .Mappings(m => m.Map<T>(mm => mm.AutoMap())));
            if (result.Acknowledged)
            {
                await EsClient.AliasAsync(al => al.Add(add => add.Index(newName).Alias(indexName)));
                return;
            }
            throw new ElasticSearchException($"Create Index {indexName} failed : :" + result.ServerError.Error.Reason);
        }

        /// <summary>
        /// AddOrUpdate Document
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TKey"></typeparam>
        /// <param name="indexName"></param>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual async Task AddOrUpdateAsync<T, TKey>(string indexName, T model) where T : EntityDto<TKey>
        {
            var exis = EsClient.DocumentExists(DocumentPath<T>.Id(new Id(model)), dd => dd.Index(indexName));

            if (exis.Exists)
            {
                var result = await EsClient.UpdateAsync(DocumentPath<T>.Id(new Id(model)),
                    ss => ss.Index(indexName).Doc(model).RetryOnConflict(3));

                if (result.ServerError == null) return;
                throw new ElasticSearchException($"Update Document failed at index{indexName} :" + result.ServerError.Error.Reason);
            }
            else
            {
                var result = await EsClient.IndexAsync<T>(model, ss => ss.Index(indexName));
                if (result.ServerError == null) return;
                throw new ElasticSearchException($"Insert Docuemnt failed at index {indexName} :" + result.ServerError.Error.Reason);
            }
        }

        /// <summary>
        /// Bulk AddOrUpdate Docuemnt,Default bulkNum is 1000
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TKey"></typeparam>
        /// <param name="indexName"></param>
        /// <param name="list"></param>
        /// <param name="bulkNum">bulkNum</param>
        /// <returns></returns>
        public virtual async Task BulkAddorUpdateAsync<T, TKey>(string indexName, List<T> list, int bulkNum = 1000) where T : EntityDto<TKey>
        {
            if (list.Count <= bulkNum)
                await BulkAddOrUpdate<T, TKey>(indexName, list);
            else
            {

                var total = (int)Math.Ceiling(list.Count * 1.0f / bulkNum);
                var tasks = new List<Task>();
                for (var i = 0; i < total; i++)
                {
                    var i1 = i;
                    tasks.Add(Task.Factory.StartNew(() => BulkAddOrUpdate<T, TKey>(indexName, list.Skip(i1 * bulkNum).Take(bulkNum).ToList()))); ;
                }
                await Task.WhenAll(tasks.ToArray());

            }
        }

        private async Task BulkAddOrUpdate<T, TKey>(string indexName, List<T> list) where T : EntityDto<TKey>
        {
            var bulk = new BulkRequest(indexName)
            {
                Operations = new List<IBulkOperation>()
            };
            foreach (var item in list)
            {
                bulk.Operations.Add(new BulkIndexOperation<T>(item));
            }
            var response = await EsClient.BulkAsync(bulk);
            if (response.Errors)
                throw new ElasticSearchException($"Bulk InsertOrUpdate Docuemnt failed at index {indexName} :{response.ServerError.Error.Reason}");

        }

        private async Task BulkDelete<T, TKey>(string indexName, List<T> list) where T : EntityDto<TKey>
        {

            var bulk = new BulkRequest(indexName)
            {
                Operations = new List<IBulkOperation>()
            };
            foreach (var item in list)
            {
                bulk.Operations.Add(new BulkDeleteOperation<T>(new Id(item)));
            }
            var response = await EsClient.BulkAsync(bulk);
            if (response.Errors)
                throw new ElasticSearchException($"Bulk Delete Docuemnt at index {indexName} :{response.ServerError.Error.Reason}");
        }

        /// <summary>
        ///  Bulk Delete Docuemnt,Default bulkNum is 1000
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TKey"></typeparam>
        /// <param name="indexName"></param>
        /// <param name="list"></param>
        /// <param name="bulkNum">bulkNum</param>
        /// <returns></returns>
        public virtual async Task BulkDeleteAsync<T, TKey>(string indexName, List<T> list, int bulkNum = 100) where T : EntityDto<TKey>
        {
            if (list.Count <= bulkNum)
                await BulkDelete<T, TKey>(indexName, list);
            else
            {
                var total = (int)Math.Ceiling(list.Count * 1.0f / bulkNum);
                var tasks = new List<Task>();
                for (var i = 0; i < total; i++)
                {
                    var i1 = i;
                    tasks.Add(Task.Factory.StartNew(() => BulkDelete<T, TKey>(indexName, list.Skip(i1 * bulkNum).Take(bulkNum).ToList())));
                }
                await Task.WhenAll(tasks);

            }
        }

        /// <summary>
        /// Delete Docuemnt
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TKey"></typeparam>
        /// <param name="indexName"></param>
        /// <param name="typeName"></param>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual async Task DeleteAsync<T, TKey>(string indexName, string typeName, T model) where T : EntityDto<TKey>
        {
            var response = await EsClient.DeleteAsync(new DeleteRequest(indexName, typeName, new Id(model)));
            if (response.ServerError == null) return ;
            throw new ElasticSearchException($"Delete Docuemnt at index {indexName} :{response.ServerError.Error.Reason}");
         
        }

        /// <summary>
        /// Delete Index
        /// </summary>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public virtual async Task DeleteIndexAsync(string indexName)
        {
            var response = await EsClient.DeleteIndexAsync(indexName);
            if (response.Acknowledged) return ;
            throw new ElasticSearchException($"Delete index {indexName} failed :{response.ServerError.Error.Reason}");
        }

        public virtual async Task ReIndex<T, TKey>(string indexName) where T : EntityDto<TKey>
        {
            await DeleteIndexAsync(indexName);
            await CreateIndexAsync<T, TKey>(indexName);
        }
        /// <summary>
        /// Non-stop Update Doucments
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TKey"></typeparam>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public virtual async Task ReBuild<T, TKey>(string indexName) where T : EntityDto<TKey>
        {
            var result = await EsClient.GetAliasAsync(q => q.Index(indexName));
            var oldName = result.Indices.Keys.FirstOrDefault();
            
            if (oldName == null)
            {
                throw new ElasticSearchException($"not found index {indexName}");
            }
            //创建新的索引
            var newIndex = indexName + DateTime.Now.Ticks;
            var createResult = await EsClient.CreateIndexAsync(newIndex,
                c =>
                    c.Index(newIndex)
                        .Mappings(ms => ms.Map<T>(m => m.AutoMap())));
            if (!createResult.Acknowledged)
            {
                throw new ElasticSearchException($"reBuild create newIndex {indexName} failed :{result.ServerError.Error.Reason}");
            }
            //重建索引数据
            var reResult = await EsClient.ReindexOnServerAsync(descriptor => descriptor.Source(source => source.Index(indexName))
                .Destination(dest => dest.Index(newIndex)));

            if (reResult.ServerError != null)
            {
                throw new ElasticSearchException($"reBuild {indexName} datas failed :{reResult.ServerError.Error.Reason}");
            }

            //删除旧索引
            var alReuslt = await EsClient.AliasAsync(al => al.Remove(rem => rem.Index(oldName.Name).Alias(indexName)).Add(add => add.Index(newIndex).Alias(indexName)));

            if (!alReuslt.Acknowledged)
            {
                throw new ElasticSearchException($"reBuild set Alias {indexName}  failed :{alReuslt.ServerError.Error.Reason}");
            }
            var delResult = await EsClient.DeleteIndexAsync(oldName);
            throw new ElasticSearchException($"reBuild delete old Index {oldName.Name} failed :" + delResult.ServerError.Error.Reason);
        }

        /// <summary>
        /// search
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TKey"></typeparam>
        /// <param name="indexName"></param>
        /// <param name="query"></param>
        /// <param name="skip">skip num</param>
        /// <param name="size">return document size</param>
        /// <param name="includeFields">return fields</param>
        /// <param name="preTags">Highlight tags</param>
        /// <param name="postTags">Highlight tags</param>
        /// <param name="disableHigh"></param>
        /// <param name="highField">Highlight fields</param>
        /// <returns></returns>
        public virtual async Task<ISearchResponse<T>> SearchAsync<T, TKey>(string indexName, SearchDescriptor<T> query, int skip, int size, string[] includeFields = null,
            string preTags = "<strong style=\"color: red;\">", string postTags = "</strong>", bool disableHigh = false, params string[] highField) where T : EntityDto<TKey>
        {
            query.Index(indexName);
            var highdes = new HighlightDescriptor<T>();
            if (disableHigh)
            {
                preTags = "";
                postTags = "";
            }
            highdes.PreTags(preTags).PostTags(postTags);

            var ishigh = highField != null && highField.Length > 0;

            var hfs = new List<Func<HighlightFieldDescriptor<T>, IHighlightField>>();

            //分页
            query.Skip(skip).Take(size);
            //关键词高亮
            if (ishigh)
            {
                foreach (var s in highField)
                {
                    hfs.Add(f => f.Field(s));
                }
            }

            highdes.Fields(hfs.ToArray());
            query.Highlight(h => highdes);
            if (includeFields != null)
                query.Source(ss => ss.Includes(ff => ff.Fields(includeFields.ToArray())));
            var response = await EsClient.SearchAsync<T>(query);
            return response;
        }
    }
}
