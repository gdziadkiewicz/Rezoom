﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace Data.Resumption.DataTasks
{
    /// <summary>
    /// Represents a set of tasks whose results will be combined into a sum in order of completion.
    /// Can be more efficient than folding a deeply nested `ApplicativeTask`.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TSum"></typeparam>
    internal class SumTask<T, TSum> : IDataTask<TSum>
    {
        private readonly IEnumerable<IDataTask<T>> _tasks;
        private readonly TSum _accumulator;
        private readonly Func<TSum, T, TSum> _add;

        public SumTask(IEnumerable<IDataTask<T>> tasks, TSum accumulator, Func<TSum, T, TSum> add)
        {
            _tasks = tasks;
            _accumulator = accumulator;
            _add = add;
        }

        public StepState<TSum> Step()
        {
            var sum = _accumulator;
            var pendings = new List<RequestsPending<T>>();
            var stepped = new List<StepState<T>>();
            var failed = new List<Exception>();
            foreach (var task in _tasks)
            {
                try
                {
                    stepped.Add(task.Step());
                }
                catch (Exception ex)
                {
                    failed.Add(ex);
                }
            }
            if (failed.Count > 0)
            {
                var cause = new AggregateException(failed);
                stepped.AbortMany(cause);
                throw cause;
            }
            foreach (var step in stepped)
            {
                step.Match(pending =>
                {
                    pendings.Add(pending);
                    return default(TSum);
                }, result => sum = _add(sum, result));
            }
            if (pendings.Count <= 0) return StepState.Result(sum);
            var sumPending = new RequestsPending<TSum>
                ( new BatchBranchN<IDataRequest>(pendings.Select(p => p.Requests).ToList())
                , response =>
                {
                    var branchN = response.AssumeBranchN();
                    var subSucceeded = new List<IDataTask<T>>();
                    var subFailed = new List<Exception>();
                    for (var i = 0; i < pendings.Count; i++)
                    {
                        var subResponse = branchN.Children[i];
                        try
                        {
                            subSucceeded.Add(pendings[i].Resume(subResponse));
                        }
                        catch (Exception ex)
                        {
                            subFailed.Add(ex);
                        }
                    }
                    if (subFailed.Count <= 0) return new SumTask<T, TSum>(subSucceeded, sum, _add);
                    var cause = new AggregateException(subFailed);
                    subSucceeded.AbortMany(cause);
                    throw cause;
                });
            return StepState.Pending(sumPending);
        }
    }
}
