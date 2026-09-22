namespace Oftp4Net.DataLayer.Filters;

public interface IFilter<TEntity> where TEntity : class
{
    IQueryable<TEntity> Apply(IQueryable<TEntity> data);
}