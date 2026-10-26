// The pinned Studio CLI has no bind-host option. Restrict its HTTP listener to
// loopback without modifying node_modules or exposing the local media over LAN.
const http = require('node:http');
const listen = http.Server.prototype.listen;
http.Server.prototype.listen = function (options, ...rest) {
    if (options && typeof options === 'object' && 'port' in options) {
        return listen.call(this, { ...options, host: '127.0.0.1' }, ...rest);
    }
    return listen.call(this, options, ...rest);
};
