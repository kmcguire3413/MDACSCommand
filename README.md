This repository holds the source for the command and control microservice.

This service provides a centralized queue of commands which clients or other
services can create. Services fetch commands using a hybrid of polling and
blocking over HTTP/S, execute the commands, then upload the result.

This allows services to not be exposed since they connect outward to the
command and control service. Each service also individually authenticates
each command which helps to keep multiple layers or points of security 
possible. 

In a nutshell, this services provides a centralized set of command and result
queues with minimal authentication and validation of commands and results.
