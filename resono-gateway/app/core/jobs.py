import asyncio
import logging
from typing import Any, Callable, Coroutine
from dataclasses import dataclass, field

logger = logging.getLogger("resono.jobs")

@dataclass
class JobState:
    job_id: str
    track_id: str
    future: asyncio.Future
    listeners: int = 1
    status: str = "IN_PROGRESS"
    result: Any = None
    error: Exception | None = None

class JobManager:
    """
    Request Deduplication & Coalescing Engine.
    Ensures that multiple concurrent resolution/download requests for the same track
    attach to a single in-flight asynchronous execution rather than duplicating work.
    """
    def __init__(self):
        self._active_jobs: dict[str, JobState] = {}
        self._lock = asyncio.Lock()

    async def get_or_create(
        self,
        track_id: str,
        job_coro_fn: Callable[[], Coroutine[Any, Any, Any]]
    ) -> Any:
        async with self._lock:
            if track_id in self._active_jobs:
                job = self._active_jobs[track_id]
                job.listeners += 1
                logger.info(f"Coalescing request for track '{track_id}' to existing job '{job.job_id}' (active listeners: {job.listeners})")
                future = job.future
            else:
                loop = asyncio.get_running_loop()
                future = loop.create_future()
                job_id = f"job-{track_id}"
                job = JobState(job_id=job_id, track_id=track_id, future=future, listeners=1)
                self._active_jobs[track_id] = job
                logger.info(f"Created new job '{job_id}' for track '{track_id}'")
                # Spawn background task to execute
                asyncio.create_task(self._run_job(job, job_coro_fn))

        # Await completion
        try:
            return await asyncio.shield(future)
        finally:
            async with self._lock:
                if track_id in self._active_jobs:
                    self._active_jobs[track_id].listeners -= 1

    async def _run_job(self, job: JobState, coro_fn: Callable[[], Coroutine[Any, Any, Any]]):
        try:
            result = await coro_fn()
            job.status = "COMPLETED"
            job.result = result
            if not job.future.done():
                job.future.set_result(result)
        except Exception as e:
            logger.error(f"Job '{job.job_id}' failed: {e}", exc_info=True)
            job.status = "FAILED"
            job.error = e
            if not job.future.done():
                job.future.set_exception(e)
        finally:
            async with self._lock:
                # Clean up active job tracking once finished
                self._active_jobs.pop(job.track_id, None)

job_manager = JobManager()
