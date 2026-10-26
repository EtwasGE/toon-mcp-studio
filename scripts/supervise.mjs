// POSIX parent-death guard. Windows uses a kernel kill-on-close Job Object.
import {spawn} from 'node:child_process';
const [owner,exe,...args]=process.argv.slice(2);
const child=spawn(exe,args,{stdio:['ignore','inherit','inherit'],detached:true,shell:false});
function kill(){try{process.kill(-child.pid,'SIGKILL');}catch{} }
const timer=setInterval(()=>{try{process.kill(Number(owner),0);}catch{kill();process.exit(143);}},250);
process.on('SIGTERM',()=>{kill();process.exit(143);});process.on('SIGINT',()=>{kill();process.exit(130);});
child.on('error',()=>{clearInterval(timer);process.exit(127);});child.on('exit',code=>{clearInterval(timer);process.exit(code??1);});
