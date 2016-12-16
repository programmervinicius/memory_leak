FROM microsoft/dotnet:1.1.0-sdk-projectjson

RUN useradd -s /bin/bash -m test

COPY ./global.json /home/test/app/global.json
COPY ./src/MemoryLeak /home/test/app/proj

RUN chown -R test: /home/test/

USER test

WORKDIR /home/test/app/proj

RUN ["dotnet", "restore"]

ENTRYPOINT ["dotnet", "run", "-c", "Release", "-p", "project.json"]