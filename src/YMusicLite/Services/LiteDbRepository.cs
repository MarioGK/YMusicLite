using LiteDB;
using YMusicLite.Models;

namespace YMusicLite.Services;

public interface IRepository<T> where T : class
{
    Task<ObjectId> InsertAsync(T entity);
    Task<ObjectId> CreateAsync(T entity);
    Task<bool> UpdateAsync(T entity);
    Task<bool> DeleteAsync(ObjectId id);
    Task<bool> DeleteAsync(string id);
    Task<T?> GetByIdAsync(ObjectId id);
    Task<T?> GetByIdAsync(string id);
    Task<IEnumerable<T>> GetAllAsync();
    Task<List<T>> FindAllAsync(System.Linq.Expressions.Expression<System.Func<T, bool>> predicate);
    Task<IEnumerable<T>> FindAsync(System.Linq.Expressions.Expression<System.Func<T, bool>> predicate);
    Task<T?> FindOneAsync(System.Linq.Expressions.Expression<System.Func<T, bool>> predicate);
}

public class LiteDbRepository<T> : IRepository<T> where T : class
{
    private readonly ILiteCollection<T> _collection;

    public LiteDbRepository(ILiteDatabase database)
    {
        _collection = database.GetCollection<T>();
    }

    public Task<ObjectId> InsertAsync(T entity)
    {
        var bsonValue = _collection.Insert(entity);
        return Task.FromResult(bsonValue.AsObjectId);
    }

    public Task<ObjectId> CreateAsync(T entity)
    {
        return InsertAsync(entity);
    }

    public Task<bool> UpdateAsync(T entity)
    {
        var result = _collection.Update(entity);
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(ObjectId id)
    {
        var result = _collection.Delete(id);
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(string id)
    {
        try
        {
            var objectId = new LiteDB.ObjectId(id);
            return DeleteAsync(objectId);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<T?> GetByIdAsync(ObjectId id)
    {
        var entity = _collection.FindById(id);
        return Task.FromResult(entity);
    }

    public Task<T?> GetByIdAsync(string id)
    {
        try
        {
            var objectId = new LiteDB.ObjectId(id);
            return GetByIdAsync(objectId);
        }
        catch
        {
            return Task.FromResult<T?>(null);
        }
    }

    public Task<IEnumerable<T>> GetAllAsync()
    {
        var entities = _collection.FindAll();
        return Task.FromResult(entities);
    }

    public Task<List<T>> FindAllAsync(System.Linq.Expressions.Expression<System.Func<T, bool>> predicate)
    {
        var entities = _collection.Find(predicate).ToList();
        return Task.FromResult(entities);
    }

    public Task<IEnumerable<T>> FindAsync(System.Linq.Expressions.Expression<System.Func<T, bool>> predicate)
    {
        var entities = _collection.Find(predicate);
        return Task.FromResult(entities);
    }

    Task<T?> IRepository<T>.FindOneAsync(System.Linq.Expressions.Expression<System.Func<T, bool>> predicate)
    {
        var entity = _collection.FindOne(predicate);
        return Task.FromResult(entity);
    }
}