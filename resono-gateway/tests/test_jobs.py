import asyncio
import pytest
from app.core.jobs import JobManager

@pytest.mark.asyncio
async def test_job_coalescing():
    manager = JobManager()
    execution_counter = 0

    async def expensive_work():
        nonlocal execution_counter
        execution_counter += 1
        await asyncio.sleep(0.05)
        return "download_data_123"

    # Launch 5 concurrent requests for the same track
    results = await asyncio.gather(
        manager.get_or_create("track_abc", expensive_work),
        manager.get_or_create("track_abc", expensive_work),
        manager.get_or_create("track_abc", expensive_work),
        manager.get_or_create("track_abc", expensive_work),
        manager.get_or_create("track_abc", expensive_work),
    )

    # All 5 callers must receive the exact same result
    assert results == ["download_data_123"] * 5
    # But the underlying operation MUST have executed exactly once!
    assert execution_counter == 1
