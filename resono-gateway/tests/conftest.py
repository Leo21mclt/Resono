import pytest
import pytest_asyncio
from app.db.database import init_db

@pytest_asyncio.fixture(autouse=True, scope='session')
async def prepare_database():
    await init_db()

