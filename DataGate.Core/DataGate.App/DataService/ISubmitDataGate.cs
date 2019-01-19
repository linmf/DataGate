﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace DataGate.App.DataService
{
    /// <summary>
    /// 数据更改前的过滤器,主要用于参数检查，或添加某些额外的参数
    /// </summary>
    public interface ISubmitDataGate:IDataGate
    {
        void OnAdd(List<string> fields, IDictionary<string, object> ps);
        void OnChange(List<string> fields, IDictionary<string, object> ps);
        void OnRemove(IDictionary<string, object> ps);
    }
}