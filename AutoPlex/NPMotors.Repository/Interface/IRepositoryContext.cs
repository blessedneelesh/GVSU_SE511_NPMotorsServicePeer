using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace NPMotors.Repository.Interface
{
    public interface IRepositoryContext
    {
        IDbConnection GetDatabaseConnection();
    }
}
