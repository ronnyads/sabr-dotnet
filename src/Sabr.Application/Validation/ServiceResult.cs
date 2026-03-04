using System.Collections.Generic;

namespace Sabr.Application.Validation;

public sealed class ServiceResult<T>
{
    public bool Succeeded { get; private set; }
    public T? Data { get; private set; }
    public List<ValidationError> Errors { get; } = new();

    public static ServiceResult<T> Success(T data)
    {
        return new ServiceResult<T> { Succeeded = true, Data = data };
    }

    public static ServiceResult<T> Failure(IEnumerable<ValidationError> errors)
    {
        var result = new ServiceResult<T> { Succeeded = false };
        result.Errors.AddRange(errors);
        return result;
    }
}
