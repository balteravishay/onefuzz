﻿using ApiService.OneFuzzLib.Orm;

namespace Microsoft.OneFuzz.Service;

public interface ITaskOperations : IStatefulOrm<Task, TaskState> {
    Async.Task<Task?> GetByTaskId(Guid taskId);

    IAsyncEnumerable<Task> GetByTaskIds(IEnumerable<Guid> taskId);

    IAsyncEnumerable<Task> GetByJobId(Guid jobId);

    Async.Task<Task?> GetByJobIdAndTaskId(Guid jobId, Guid taskId);


    IAsyncEnumerable<Task> SearchStates(Guid? jobId = null, IEnumerable<TaskState>? states = null);

    IEnumerable<string>? GetInputContainerQueues(TaskConfig config);

    IAsyncEnumerable<Task> SearchExpired();
    Async.Task MarkStopping(Task task);
    Async.Task MarkFailed(Task task, Error error, List<Task>? taskInJob = null);

    Async.Task<TaskVm?> GetReproVmConfig(Task task);
    Async.Task<bool> CheckPrereqTasks(Task task);
    Async.Task<Pool?> GetPool(Task task);
    Async.Task<Task> SetState(Task task, TaskState state);
}

public class TaskOperations : StatefulOrm<Task, TaskState, TaskOperations>, ITaskOperations {


    public TaskOperations(ILogTracer log, IOnefuzzContext context)
        : base(log, context) {

    }

    public async Async.Task<Task?> GetByTaskId(Guid taskId) {
        return await GetByTaskIds(new[] { taskId }).FirstOrDefaultAsync();
    }

    public IAsyncEnumerable<Task> GetByTaskIds(IEnumerable<Guid> taskId) {
        return QueryAsync(filter: Query.RowKeys(taskId.Select(t => t.ToString())));
    }

    public IAsyncEnumerable<Task> GetByJobId(Guid jobId) {
        return QueryAsync(filter: $"PartitionKey eq '{jobId}'");
    }

    public async Async.Task<Task?> GetByJobIdAndTaskId(Guid jobId, Guid taskId) {
        var data = QueryAsync(filter: $"PartitionKey eq '{jobId}' and RowKey eq '{taskId}'");

        return await data.FirstOrDefaultAsync();
    }
    public IAsyncEnumerable<Task> SearchStates(Guid? jobId = null, IEnumerable<TaskState>? states = null) {
        var queryString =
            (jobId, states) switch {
                (null, null) => "",
                (Guid id, null) => Query.PartitionKey($"{id}"),
                (null, IEnumerable<TaskState> s) => Query.EqualAnyEnum("state", s),
                (Guid id, IEnumerable<TaskState> s) => Query.And(Query.PartitionKey($"{id}"), Query.EqualAnyEnum("state", s)),
            };

        return QueryAsync(filter: queryString);
    }

    public IEnumerable<string>? GetInputContainerQueues(TaskConfig config) {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Task> SearchExpired() {
        var timeFilter = $"end_time lt datetime'{DateTimeOffset.UtcNow.ToString("o")}'";
        var stateFilter = Query.EqualAnyEnum("state", TaskStateHelper.AvailableStates);
        var filter = Query.And(stateFilter, timeFilter);
        return QueryAsync(filter: filter);
    }

    public async Async.Task MarkStopping(Task task) {
        if (task.State.ShuttingDown()) {
            _logTracer.Verbose($"ignoring post - task stop calls to stop {task.JobId}:{task.TaskId}");
            return;
        }

        if (!task.State.HasStarted()) {
            await MarkFailed(task, new Error(Code: ErrorCode.TASK_FAILED, Errors: new[] { "task never started" }));
        } else {
            await SetState(task, TaskState.Stopping);
        }
    }

    public async Async.Task MarkFailed(Task task, Error error, List<Task>? taskInJob = null) {
        if (task.State.ShuttingDown()) {
            _logTracer.Verbose(
                $"ignoring post-task stop failures for {task.JobId}:{task.TaskId}"
            );
            return;
        }

        if (task.Error != null) {
            _logTracer.Verbose(
                $"ignoring additional task error {task.JobId}:{task.TaskId}"
            );
            return;
        }

        _logTracer.Error($"task failed {task.JobId}:{task.TaskId} - {error}");

        task = await SetState(task with { Error = error }, TaskState.Stopping);
        //self.set_state(TaskState.stopping)
        await MarkDependantsFailed(task, taskInJob);
    }

    private async Async.Task MarkDependantsFailed(Task task, List<Task>? taskInJob = null) {
        taskInJob ??= await SearchByPartitionKeys(new[] { $"{task.JobId}" }).ToListAsync();

        foreach (var t in taskInJob) {
            if (t.Config.PrereqTasks != null) {
                if (t.Config.PrereqTasks.Contains(t.TaskId)) {
                    await MarkFailed(task, new Error(ErrorCode.TASK_FAILED, new[] { $"prerequisite task failed.  task_id:{t.TaskId}" }), taskInJob);
                }
            }
        }
    }

    public async Async.Task<Task> SetState(Task task, TaskState state) {
        if (task.State == state) {
            return task;
        }

        if (task.State == TaskState.Running || task.State == TaskState.SettingUp) {
            task = await OnStart(task with { State = state });
        } else {
            task = task with { State = state };
        }

        await this.Replace(task);
        var _events = _context.Events;
        if (task.State == TaskState.Stopped) {
            if (task.Error != null) {
                await _events.SendEvent(new EventTaskFailed(
                    JobId: task.JobId,
                    TaskId: task.TaskId,
                    Error: task.Error,
                    UserInfo: task.UserInfo,
                    Config: task.Config)
                    );
            } else {
                await _events.SendEvent(new EventTaskStopped(
                   JobId: task.JobId,
                   TaskId: task.TaskId,
                   UserInfo: task.UserInfo,
                   Config: task.Config)
                   );
            }
        } else {
            await _events.SendEvent(new EventTaskStateUpdated(
                   JobId: task.JobId,
                   TaskId: task.TaskId,
                   State: task.State,
                   EndTime: task.EndTime,
                   Config: task.Config)
                   );
        }

        return task;
    }

    private async Async.Task<Task> OnStart(Task task) {
        if (task.EndTime == null) {
            task = task with { EndTime = DateTimeOffset.UtcNow + TimeSpan.FromHours(task.Config.Task.Duration) };

            var jobOperations = _context.JobOperations;
            Job? job = await jobOperations.Get(task.JobId);
            if (job != null) {
                await jobOperations.OnStart(job);
            }

        }

        return task;

    }

    public async Async.Task<TaskVm?> GetReproVmConfig(Task task) {
        if (task.Config.Vm != null) {
            return task.Config.Vm;
        }

        if (task.Config.Pool == null) {
            throw new Exception($"either pool or vm must be specified: {task.TaskId}");
        }

        var pool = await _context.PoolOperations.GetByName(task.Config.Pool.PoolName);

        if (!pool.IsOk) {
            _logTracer.Info($"unable to find pool from task: {task.TaskId}");
            return null;
        }

        var scaleset = await _context.ScalesetOperations.SearchByPool(task.Config.Pool.PoolName).FirstOrDefaultAsync();

        if (scaleset == null) {
            _logTracer.Warning($"no scalesets are defined for task: {task.JobId}:{task.TaskId}");
            return null;
        }

        return new TaskVm(scaleset.Region, scaleset.VmSku, scaleset.Image, null);
    }

    public async Async.Task<bool> CheckPrereqTasks(Task task) {
        if (task.Config.PrereqTasks != null) {
            foreach (var taskId in task.Config.PrereqTasks) {
                var t = await GetByTaskId(taskId);

                // if a prereq task fails, then mark this task as failed
                if (t == null) {
                    await MarkFailed(task, new Error(ErrorCode.INVALID_REQUEST, Errors: new[] { "unable to find prereq task" }));
                    return false;
                }

                if (!t.State.HasStarted()) {
                    return false;
                }
            }
        }
        return true;
    }

    public async Async.Task<Pool?> GetPool(Task task) {
        if (task.Config.Pool != null) {
            var pool = await _context.PoolOperations.GetByName(task.Config.Pool.PoolName);
            if (!pool.IsOk) {
                _logTracer.Info(
                    $"unable to schedule task to pool: {task.TaskId} - {pool.ErrorV}"
                );
                return null;
            }
            return pool.OkV;
        } else if (task.Config.Vm != null) {
            var scalesets = _context.ScalesetOperations.Search().Where(s => s.VmSku == task.Config.Vm.Sku && s.Image == task.Config.Vm.Image);

            await foreach (var scaleset in scalesets) {
                if (task.Config.Pool == null) {
                    continue;
                }
                var pool = await _context.PoolOperations.GetByName(task.Config.Pool.PoolName);
                if (!pool.IsOk) {
                    _logTracer.Info(
                        $"unable to schedule task to pool: {task.TaskId} - {pool.ErrorV}"
                    );
                    return null;
                }
                return pool.OkV;
            }
        }

        _logTracer.Warning($"unable to find a scaleset that matches the task prereqs: {task.TaskId}");
        return null;

    }

    public async Async.Task<Task> Init(Task task) {
        await _context.Queue.CreateQueue($"{task.TaskId}", StorageType.Corpus);
        return await SetState(task, TaskState.Waiting);
    }


    public async Async.Task<Task> Stopping(Task task) {
        _logTracer.Info($"stopping task : {task.JobId}, {task.TaskId}");
        await _context.NodeOperations.StopTask(task.TaskId);
        var anyRemainingNodes = await _context.NodeTasksOperations.GetNodesByTaskId(task.TaskId).AnyAsync();
        if (!anyRemainingNodes) {
            return await Stopped(task);
        }
        return task;
    }

    private async Async.Task<Task> Stopped(Task inputTask) {
        var task = await SetState(inputTask, TaskState.Stopped);
        await _context.Queue.DeleteQueue($"{task.TaskId}", StorageType.Corpus);

        //     # TODO: we need to 'unschedule' this task from the existing pools
        var job = await _context.JobOperations.Get(task.JobId);
        if (job != null) {
            await _context.JobOperations.StopIfAllDone(job);
        }

        return task;
    }
}
