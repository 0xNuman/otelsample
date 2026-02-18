FROM python:3.12-slim

# pymssql ships manylinux wheels for linux/aarch64, so no system libs needed.
RUN pip install pymssql --no-cache-dir

COPY db-init.py /db-init.py
ENTRYPOINT ["python", "/db-init.py"]
