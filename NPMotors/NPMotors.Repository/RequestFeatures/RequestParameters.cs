using System;
using System.Collections.Generic;
using System.Text;

namespace NPMotors.Repository.RequestFeatures
{
    public abstract class RequestParameters
    {
        const int maxPageSize = 100;
        public int PageNumber { get; set; } = 1;
        private int _pageSize = 5;
        public int PageSize
        {
            get
            {
                return _pageSize;
            }
            set
            {
                _pageSize = (value > maxPageSize) ? maxPageSize : value;
            }
        }
    }
}
