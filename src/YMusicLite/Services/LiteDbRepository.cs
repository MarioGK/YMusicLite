using LiteDB;
using YMusicLite.Models;

namespace YMusicLite.Services;

public interface IRepository<T> where T : class
{
    Task<ObjectId> InsertAsync(T entity);
    Task<bool> UpdateAsync(T entity);
    Task<bool> DeleteAsync(ObjectId id);
    Task<T?> GetByIdAsync(ObjectId id);
    Task<IEnumerable<T>> GetAllAsync();
    Task<IEnumerable<T>> FindAsync(System.Linq.Expressions.Expression<System.Func<T, bool>> predicate);
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

    public Task<T?> GetByIdAsync(ObjectId id)
    {
        var entity = _collection.FindById(id);
        return Task.FromResult(entity);
    }

    public Task<IEnumerable<T>> GetAllAsync()
    {
        var entities = _collection.FindAll();
        return Task.FromResult(entities);
    }

    public Task<IEnumerable<T>> FindAsync(System.Linq.Expressions.Expression<System.Func<T, bool>> predicate)
    {
        var entities = _collection.Find(predicate);
        return Task.FromResult(entities);
    }
}