/* http://www.zkea.net/ Copyright 2016 ZKEASOFT http://www.zkea.net/licenses */
using Easy;
using Easy.Constant;
using Easy.Extend;
using Easy.RepositoryPattern;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using ZKEACMS.DataArchived;
using ZKEACMS.ExtendField;
using ZKEACMS.Widget;
using Microsoft.EntityFrameworkCore;

namespace ZKEACMS.Page
{
    public class PageService : ServiceBase<PageEntity>, IPageService
    {
        private readonly IWidgetBasePartService _widgetService;
        private readonly IWidgetActivator _widgetActivator;


        public PageService(IWidgetBasePartService widgetService, IApplicationContext applicationContext, IWidgetActivator widgetActivator, CMSDbContext dbContext)
            : base(applicationContext, dbContext)
        {
            _widgetService = widgetService;
            _widgetActivator = widgetActivator;
        }
        public override DbSet<PageEntity> CurrentDbSet
        {
            get
            {
                return (DbContext as CMSDbContext).Page;
            }
        }
        public override ServiceResult<PageEntity> Add(PageEntity item)
        {
            if (!item.IsPublishedPage && Count(m => m.Url == item.Url && m.IsPublishedPage == false) > 0)
            {
                throw new PageExistException(item);
            }
            item.ID = Guid.NewGuid().ToString("N");
            if (item.ParentId.IsNullOrEmpty())
            {
                item.ParentId = "#";
            }
            return base.Add(item);
        }

        public override ServiceResult<PageEntity> Update(PageEntity item)
        {
            if (Count(m => m.ID != item.ID && m.Url == item.Url && m.IsPublishedPage == false) > 0)
            {
                throw new PageExistException(item);
            }
            item.IsPublish = false;
            return base.Update(item);
        }

        public void Publish(PageEntity item)
        {
            item = Get(item.ID);
            item.IsPublish = true;
            item.PublishDate = DateTime.Now;
            base.Update(item);

            //Remove(m => m.ReferencePageID == item.ID && m.IsPublishedPage == true);

            _widgetService.RemoveCache(item.ID);

            item.ReferencePageID = item.ID;
            item.IsPublishedPage = true;
            item.PublishDate = DateTime.Now;

            var widgets = _widgetService.GetByPageId(item.ID);
            Add(item);
            widgets.Each(m =>
            {
                using (var widgetService = _widgetActivator.Create(m))
                {
                    m = widgetService.GetWidget(m);
                    m.PageID = item.ID;
                    widgetService.IsNeedNotifyChange = false;
                    widgetService.Publish(m);
                }
            });
        }
        public void Revert(string ID, bool RetainLatest)
        {
            var page = Get(ID);
            if (page.IsPublishedPage)
            {
                var refPage = Get(page.ReferencePageID);
                refPage.IsPublish = false;
                Update(refPage);
                page.PublishDate = DateTime.Now;
                Add(page);

                var widgets = _widgetService.GetByPageId(ID);
                widgets.Each(m =>
                {
                    var widgetService = _widgetActivator.Create(m);
                    m = widgetService.GetWidget(m);
                    m.PageID = page.ID;
                    widgetService.IsNeedNotifyChange = false;
                    widgetService.Publish(m);
                });
                _widgetService.RemoveCache(page.ReferencePageID);
                if (!RetainLatest)
                {//清空当前的所有修改
                    _widgetService.GetByPageId(page.ReferencePageID).Each(m =>
                    {
                        var widgetService = _widgetActivator.Create(m);
                        widgetService.IsNeedNotifyChange = false;
                        widgetService.DeleteWidget(m.ID);
                    });
                    _widgetService.GetByPageId(ID).Each(m =>
                    {
                        var widgetService = _widgetActivator.Create(m);
                        m = widgetService.GetWidget(m);
                        m.PageID = page.ReferencePageID;
                        widgetService.IsNeedNotifyChange = false;
                        widgetService.Publish(m);
                    });
                }
            }
        }
        public override void Remove(PageEntity item)
        {
            Remove(m => m.ParentId == item.ID);
            var widgets = _widgetService.GetByPageId(item.ID);
            widgets.Each(m =>
            {
                using (var widgetService = _widgetActivator.Create(m))
                {
                    widgetService.IsNeedNotifyChange = false;
                    widgetService.DeleteWidget(m.ID);
                }
            });
            if (item.PublishDate.HasValue)
            {
                Remove(m => m.ReferencePageID == item.ID);
            }
            _widgetService.RemoveCache(item.ID);
            base.Remove(item);
        }

        public override void Remove(Expression<Func<PageEntity, bool>> filter)
        {
            var deletes = Get(filter).Select(m => m.ID);
            if (deletes.Any())
            {
                Remove(m => deletes.Contains(m.ParentId));
                Remove(m => deletes.Contains(m.ReferencePageID));

                var widgets = _widgetService.Get(m => deletes.Contains(m.PageID));
                widgets.Each(m =>
                {
                    using (var widgetService = _widgetActivator.Create(m))
                    {
                        widgetService.IsNeedNotifyChange = false;
                        widgetService.DeleteWidget(m.ID);
                    }
                });

                deletes.Each(p => _widgetService.RemoveCache(p));

                base.Remove(filter);
            }

        }
        public override void RemoveRange(params PageEntity[] items)
        {
            items.Each(m => Remove(m));
        }

        public void DeleteVersion(string ID)
        {
            PageEntity page = Get(ID);
            if (page != null)
            {
                var widgets = _widgetService.GetByPageId(page.ID);
                widgets.Each(m => _widgetActivator.Create(m).DeleteWidget(m.ID));
                _widgetService.RemoveCache(ID);
            }
            base.Remove(ID);
        }

        public void Move(string id, int position, int oldPosition)
        {
            var page = Get(id);
            page.DisplayOrder = position;

            IEnumerable<PageEntity> pages = CurrentDbSet.AsTracking().Where(m => !m.IsPublishedPage && m.ParentId == page.ParentId && m.ID != page.ID).OrderBy(m => m.DisplayOrder);

            int order = 1;
            for (int i = 0; i < pages.Count(); i++)
            {
                var eleNav = pages.ElementAt(i);
                if (i == position - 1)
                {
                    order++;
                }
                eleNav.DisplayOrder = order;
                order++;
            }

            Update(page);
        }
        public PageEntity GetByPath(string path, bool isPreView)
        {
            if (path != "/" && path.EndsWith("/"))
            {
                path = path.Substring(0, path.Length - 1);
            }
            if (path == "/")
            {
                path = "/index";
            }
            if (!path.StartsWith("~"))
            {
                path = "~" + path;
            }
            var result = CurrentDbSet
                .Where(m => m.Url == path && m.IsPublishedPage == !isPreView)
                .OrderByDescending(m => m.PublishDate)
                .FirstOrDefault();

            //if (result != null && result.ExtendFields != null)
            //{
            //    /*!
            //     * http://www.zkea.net/ 
            //     * Copyright 2017 ZKEASOFT 
            //     * http://www.zkea.net/licenses 
            //     */
            //    ((List<ExtendFieldEntity>)result.ExtendFields).Add(new ExtendFieldEntity { Title = "meta_support", Value = "ZKEASOFT" });
            //}
            return result;

        }

        public void MarkChanged(string pageId)
        {
            var pageEntity = Get(pageId);
            if (pageEntity != null)
            {
                pageEntity.IsPublish = false;
                pageEntity.LastUpdateDate = DateTime.Now;
                if (ApplicationContext.CurrentUser != null)
                {
                    pageEntity.LastUpdateBy = ApplicationContext.CurrentUser.UserID;
                    pageEntity.LastUpdateByName = ApplicationContext.CurrentUser.UserName;
                }
                base.Update(pageEntity);
            }
        }
    }
}
