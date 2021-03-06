﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Web;
using System.Web.Mvc;
using SmartStore.Core;
using SmartStore.Core.Data;
using SmartStore.Core.Domain.Catalog;
using SmartStore.Core.Domain.Customers;
using SmartStore.Core.Domain.Topics;
using SmartStore.Services.Localization;
using SmartStore.Services.Security;
using SmartStore.Services.Seo;
using SmartStore.Services.Stores;

namespace SmartStore.Services.Cms
{
    public partial class LinkResolver : ILinkResolver
    {
		/// <remarks>
		/// {0} : Expression w/o q
		/// {1} : LanguageId
		/// {2} : Store
		/// {3} : RolesIdent
		/// </remarks>
		internal const string LINKRESOLVER_KEY = "linkresolver:{0}-{1}-{2}-{3}";

		// 0: Expression
		internal const string LINKRESOLVER_PATTERN_KEY = "linkresolver:{0}-*";

		protected readonly ICommonServices _services;
        protected readonly IUrlRecordService _urlRecordService;
        protected readonly ILocalizedEntityService _localizedEntityService;
        protected readonly IAclService _aclService;
        protected readonly IStoreMappingService _storeMappingService;
        protected readonly UrlHelper _urlHelper;

        public LinkResolver(
            ICommonServices services,
            IUrlRecordService urlRecordService,
            ILocalizedEntityService localizedEntityService,
            IAclService aclService,
            IStoreMappingService storeMappingService,
            UrlHelper urlHelper)
        {
            _services = services;
            _urlRecordService = urlRecordService;
            _localizedEntityService = localizedEntityService;
            _aclService = aclService;
            _storeMappingService = storeMappingService;
            _urlHelper = urlHelper;

            QuerySettings = DbQuerySettings.Default;
        }

        public DbQuerySettings QuerySettings { get; set; }

        public virtual LinkResolverResult Resolve(string linkExpression, IEnumerable<CustomerRole> roles = null, int languageId = 0, int storeId = 0)
        {
			if (linkExpression.IsEmpty())
			{
				return new LinkResolverResult { Type = LinkType.Url, Status = LinkStatus.NotFound };
			}

			if (roles == null)
            {
                roles = _services.WorkContext.CurrentCustomer.CustomerRoles;
            }

            if (languageId == 0)
            {
                languageId = _services.WorkContext.WorkingLanguage.Id;
            }

            if (storeId == 0)
            {
                storeId = _services.StoreContext.CurrentStore.Id;
            }

			var d = Parse(linkExpression);
            var queryString = d.QueryString;

			if (d.Type == LinkType.Url)
			{
				var url = d.Value.ToString();
				if (url.EmptyNull().StartsWith("~"))
				{
					url = VirtualPathUtility.ToAbsolute(url);
				}
				d.Link = d.Label = url;
			}
			else if (d.Type == LinkType.File)
			{
				d.Link = d.Label = d.Value.ToString();
			}
			else
			{
				var cacheKey = LINKRESOLVER_KEY.FormatInvariant(
					d.Expression,
					languageId,
					storeId,
					string.Join(",", roles.Where(x => x.Active).Select(x => x.Id)));

				d = _services.Cache.Get(cacheKey, () =>
				{
					var d2 = d.Clone();

					switch (d2.Type)
					{
						case LinkType.Product:
							GetEntityData<Product>(d2, languageId, x => new ResolverEntitySummary
							{
								Name = x.Name,
								Published = x.Published,
								Deleted = x.Deleted,
								SubjectToAcl = x.SubjectToAcl,
								LimitedToStores = x.LimitedToStores,
                                PictureId = x.MainPictureId
							});
							break;
						case LinkType.Category:
							GetEntityData<Category>(d2, languageId, x => new ResolverEntitySummary
							{
								Name = x.Name,
								Published = x.Published,
								Deleted = x.Deleted,
								SubjectToAcl = x.SubjectToAcl,
								LimitedToStores = x.LimitedToStores,
                                PictureId = x.PictureId
							});
							break;
						case LinkType.Manufacturer:
							GetEntityData<Manufacturer>(d2, languageId, x => new ResolverEntitySummary
							{
								Name = x.Name,
								Published = x.Published,
								Deleted = x.Deleted,
								LimitedToStores = x.LimitedToStores,
                                PictureId = x.PictureId
							});
							break;
						case LinkType.Topic:
							GetEntityData<Topic>(d2, languageId, x => null);
							break;
						default:
							throw new SmartException("Unknown link builder type.");
					}

					return d2;
				});
			}

            var result = new LinkResolverResult
            {
                Type = d.Type,
                Status = d.Status,
                Value = d.Value,
                Link = d.Link,
                QueryString = queryString,
                Label = d.Label,
                Id = d.Id,
                PictureId = d.PictureId
            };

            // Check ACL and limited to stores.
            switch (d.Type)
            {
                case LinkType.Product:
                case LinkType.Category:
                case LinkType.Manufacturer:
                case LinkType.Topic:
                    var entityName = d.Type.ToString();
                    if (d.LimitedToStores && d.Status == LinkStatus.Ok && !QuerySettings.IgnoreMultiStore && !_storeMappingService.Authorize(entityName, d.Id, storeId))
                    {
                        result.Status = LinkStatus.NotFound;
                    }
                    else if (d.SubjectToAcl && d.Status == LinkStatus.Ok && !QuerySettings.IgnoreAcl && !_aclService.Authorize(entityName, d.Id, roles))
                    {
                        result.Status = LinkStatus.Forbidden;
                    }
                    break;
            }

            return result;
        }

		private bool TokenizeExpression(string expression, out string type, out string path, out string query)
		{
			type = null;
			path = null;
			query = null;

			if (string.IsNullOrWhiteSpace(expression))
			{
				return false;
			}

			var colonIndex = expression.IndexOf(':');
			if (colonIndex > -1)
			{
				type = expression.Substring(0, colonIndex);
				if (type.StartsWith("http", StringComparison.OrdinalIgnoreCase))
				{
					type = null;
					colonIndex = -1;
				}	
			}

			path = expression.Substring(colonIndex + 1);

			var qmIndex = path.IndexOf('?');
			if (qmIndex > -1)
			{
				query = path.Substring(qmIndex + 1);
				path = path.Substring(0, qmIndex);
			}

			return true;
		}

        protected virtual LinkResolverData Parse(string linkExpression)
        {
			if (TokenizeExpression(linkExpression, out var type, out var path, out var query))
			{
				if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse(type, true, out LinkType linkType))
				{
					var result = new LinkResolverData { Type = linkType, Expression = string.Concat(type, ":", path) };

					switch (linkType)
					{
						case LinkType.Product:
						case LinkType.Category:
						case LinkType.Manufacturer:
						case LinkType.Topic:
							if (int.TryParse(path, out var id))
							{
								// Reduce thrown exceptions in console
								result.Value = id;
							}
							else
							{
								result.Value = path;
							}

							result.QueryString = query;
							break;
						case LinkType.Url:
							result.Value = path + (query.HasValue() ? "?" + query : "");
							break;
						case LinkType.File:
							result.Value = path;
							result.QueryString = query;
							break;
						default:
							throw new SmartException("Unknown link builder type.");
					}

					return result;
				}
			}

			return new LinkResolverData { Type = LinkType.Url, Value = linkExpression.EmptyNull() };
        }

        internal void GetEntityData<T>(LinkResolverData data, int languageId, Expression<Func<T, ResolverEntitySummary>> selector) where T : BaseEntity
        {
            ResolverEntitySummary summary = null;
            string systemName = null;

            if (data.Value is string)
            {
                data.Id = 0;
                systemName = (string)data.Value;
            }
            else
            {
                data.Id = (int)data.Value;
            }

            if (data.Type == LinkType.Topic)
            {
                var query = _services.DbContext.Set<Topic>()
                    .AsNoTracking()
                    .AsQueryable();

                query = string.IsNullOrEmpty(systemName)
                    ? query.Where(x => x.Id == data.Id)
                    : query.Where(x => x.SystemName == systemName);

                summary = query.Select(x => new ResolverEntitySummary
                {
                    Id = x.Id,
                    Name = x.SystemName,
                    Published = x.IsPublished,
                    SubjectToAcl = x.SubjectToAcl,
                    LimitedToStores = x.LimitedToStores
                })
                .FirstOrDefault();
            }
            else
            {
                summary = _services.DbContext.Set<T>()
                    .AsNoTracking()
                    .Where(x => x.Id == data.Id)
                    .Select(selector)
                    .FirstOrDefault();
            }

            if (summary != null)
            {
                var entityName = data.Type.ToString();

                data.Id = summary.Id != 0 ? summary.Id : data.Id;
                data.SubjectToAcl = summary.SubjectToAcl;
                data.LimitedToStores = summary.LimitedToStores;
                data.PictureId = summary.PictureId;
                data.Status = summary.Deleted
                    ? LinkStatus.NotFound
                    : summary.Published ? LinkStatus.Ok : LinkStatus.Hidden;
				
                if (data.Type == LinkType.Topic)
                {
                    data.Label = _localizedEntityService.GetLocalizedValue(languageId, data.Id, entityName, "ShortTitle").NullEmpty() ??
                        _localizedEntityService.GetLocalizedValue(languageId, data.Id, entityName, "Title").NullEmpty() ??
                        summary.Name;
                }
                else
                {
                    data.Label = _localizedEntityService.GetLocalizedValue(languageId, data.Id, entityName, "Name").NullEmpty() ?? summary.Name;
                }

                data.Slug = _urlRecordService.GetActiveSlug(data.Id, entityName, languageId).NullEmpty() ?? _urlRecordService.GetActiveSlug(data.Id, entityName, 0);
                if (!string.IsNullOrEmpty(data.Slug))
                {
                    data.Link = _urlHelper.RouteUrl(entityName, new { SeName = data.Slug });
                }
            }
            else
            {
                data.Label = systemName;
                data.Status = LinkStatus.NotFound;
            }
        }
    }

    internal class ResolverEntitySummary
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool Deleted { get; set; }
        public bool Published { get; set; }
        public bool SubjectToAcl { get; set; }
        public bool LimitedToStores { get; set; }
        public int? PictureId { get; set; }
    }
}
