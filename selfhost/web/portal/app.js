// root/selfhost/web/portal/app.js
'use strict';
const root=document.querySelector('#root'),logout=document.querySelector('#logout'),identity=document.querySelector('#identity');
let csrf='',profile=null,state={view:'Overview',services:[],invoices:[],payments:[],tickets:[],usage:null};
const sections=['Overview','Services','Invoices','Payments','Usage','Tickets','Profile'];
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[c]));
const zar=v=>new Intl.NumberFormat('en-ZA',{style:'currency',currency:'ZAR'}).format(Number(v)||0);
const gb=v=>((Number(v)||0)/1073741824).toFixed(2)+' GB';
async function request(path,method='GET',body){
 const options={method,credentials:'same-origin',headers:{Accept:'application/json'}};
 if(method!=='GET')options.headers['X-CSRF-Token']=csrf;
 if(body!==undefined){options.headers['Content-Type']='application/json';options.body=JSON.stringify(body)}
 const response=await fetch('/api/portal'+path,options);
 let payload;try{payload=await response.json()}catch{throw Error('Portal returned an invalid response')}
 if(!response.ok)throw Error(payload.error||payload.detail||'HTTP '+response.status);
 return payload;
}
function login(){
 csrf='';profile=null;logout.hidden=true;identity.textContent='';
 root.innerHTML='<main><div class="card login"><h1>Customer sign in</h1><p class="muted">Enter the portal credentials supplied by your Internet service provider.</p><form id="login"><label for="username">Username</label><input id="username" name="username" autocomplete="username" required><label for="password">Password</label><input id="password" name="password" type="password" autocomplete="current-password" required><p id="error" class="error" role="alert"></p><button class="primary">Sign in</button></form></div></main>';
 root.querySelector('#login').onsubmit=async e=>{
  e.preventDefault();const credentials=Object.fromEntries(new FormData(e.target));
  try{const session=await request('/login','POST',credentials);csrf=session.csrf;await load()}
  catch(err){root.querySelector('#error').textContent=err.message}
 };
}
async function load(){
 try{
  const results=await Promise.all([
   request('/profile'),request('/services'),request('/invoices'),request('/payments'),request('/tickets')
  ]);
  [profile,state.services,state.invoices,state.payments,state.tickets]=results;
  identity.textContent=profile.account;logout.hidden=false;render();
 }catch(e){if(/401|unauthor/i.test(e.message))login();else root.innerHTML='<main class="card"><p class="error">'+esc(e.message)+'</p></main>'}
}
async function boot(){
 try{const session=await request('/session');csrf=session.csrf;await load()}
 catch{login()}
}
function table(rows,fields,clickable=false){
 return '<div class="card tablewrap"><table><thead><tr>'+fields.map(([name,label])=>'<th>'+esc(label)+'</th>').join('')+'</tr></thead><tbody>'+rows.map(r=>'<tr'+(clickable?' data-ticket="'+esc(r.id)+'"':'')+'>'+fields.map(([key])=>'<td>'+esc(r[key]??'—')+'</td>').join('')+'</tr>').join('')+'</tbody></table>'+(rows.length?'':'<p class="muted">No records available.</p>')+'</div>';
}
function render(){
 const c=profile;if(!c)return login();
 root.innerHTML='<main><h1>Welcome, '+esc(c.first_name)+'</h1><p class="muted">Account: '+esc(c.account)+' · '+esc(c.status)+'</p><nav class="tabs" aria-label="Portal sections">'+sections.map(v=>'<button data-view="'+esc(v)+'" class="'+(v===state.view?'active':'')+'">'+esc(v)+'</button>').join('')+'</nav><section id="content"></section></main>';
 root.querySelectorAll('[data-view]').forEach(el=>el.onclick=()=>{state.view=el.dataset.view;render()});
 const content=root.querySelector('#content');
 if(state.view==='Overview'){
  const outstanding=state.invoices.reduce((n,i)=>n+Number(i.outstanding||0),0);
  const cards=[['Subscribed services',state.services.length],['Open tickets',state.tickets.filter(t=>!['resolved','closed'].includes(t.status)).length],['Issued invoices',state.invoices.length],['Recorded outstanding balance',zar(outstanding)]];
  content.innerHTML='<div class="grid">'+cards.map(([name,value])=>'<div class="card"><small>'+esc(name)+'</small><div class="metric">'+esc(value)+'</div></div>').join('')+'</div><div class="card" style="margin-top:18px"><h2>Manage your connection</h2><p class="muted">Review your subscriptions, download usage and reported support issues.</p><div class="actions"><button class="primary" id="new-ticket">Open support ticket</button><button id="view-bills">View invoices</button></div></div><div class="note">Payment entries and balances are informational and reflect what your ISP has recorded. This portal does not process payments.</div>';
  content.querySelector('#new-ticket').onclick=newTicket;
  content.querySelector('#view-bills').onclick=()=>{state.view='Invoices';render()};
 }else if(state.view==='Services'){
  content.innerHTML='<h2>My subscriptions</h2>'+table(state.services,[['username','Connection login'],['package','Package'],['download_mbps','Download Mbps'],['upload_mbps','Upload Mbps'],['state','Status'],['created_at','Created']]);
 }else if(state.view==='Invoices'){
  content.innerHTML='<h2>Invoices</h2>'+table(state.invoices.map(i=>({...i,total:zar(i.total),paid:zar(i.paid),credited:zar(i.credited),outstanding:zar(i.outstanding)})),[['number','Invoice'],['period','Period'],['due_date','Due date'],['total','Amount'],['paid','Paid'],['credited','Credited'],['outstanding','Outstanding'],['status','Status']],true);
  content.querySelectorAll('tr[data-ticket]').forEach(row=>row.onclick=()=>openInvoice(row.dataset.ticket));
 }else if(state.view==='Payments'){
  content.innerHTML='<h2>Recorded payments</h2>'+table(state.payments.map(p=>({...p,amount:zar(p.amount),reversed:p.reversed?'Yes':'No'})),[['invoice','Invoice'],['reference','Reference'],['amount','Amount'],['received_at','Received'],['reversed','Reversed']]);
 }else if(state.view==='Usage'){
  const now=new Date().toISOString().slice(0,7);
  content.innerHTML='<div class="toolbar"><h2>Data usage</h2><label>Month <input id="month" type="month" value="'+now+'"></label></div><div class="note">Usage is estimated from RADIUS accounting intervals. Historical data from before accounting capture and counter anomalies may not appear.</div><div id="usage-result" class="card">Loading…</div>';
  const month=content.querySelector('#month'),target=content.querySelector('#usage-result');
  const loadUsage=async()=>{
   try{const report=await request('/usage?period='+encodeURIComponent(month.value));
    target.innerHTML=table(report.records.map(x=>({...x,upload_bytes:gb(x.upload_bytes),download_bytes:gb(x.download_bytes)})),[['username','Connection'],['upload_bytes','Upload'],['download_bytes','Download'],['observed_intervals','Samples']])+'<p class="muted">Counter anomalies: '+esc(report.anomalyCount)+'. All intervals use UTC; measured traffic is apportioned across calendar months.</p>';
   }catch(e){target.textContent=e.message}
  };
  month.onchange=loadUsage;loadUsage();
 }else if(state.view==='Tickets'){
  content.innerHTML='<div class="toolbar"><h2>Support tickets</h2><button class="primary" id="new-ticket">+ New ticket</button></div>'+table(state.tickets,[['number','Number'],['subject','Subject'],['status','Status'],['priority','Priority'],['created_at','Created']],true);
  content.querySelector('#new-ticket').onclick=newTicket;
  content.querySelectorAll('tr[data-ticket]').forEach(row=>row.onclick=()=>openTicket(row.dataset.ticket));
 }else if(state.view==='Profile'){
  const fields=[['Account',c.account],['Name',c.first_name+' '+c.surname],['Email',c.email],['Phone',c.phone],['Address',c.address],['Customer status',c.status]];
  content.innerHTML='<div class="grid"><div class="card"><h2>Your account</h2>'+fields.map(([k,v])=>'<p><small>'+esc(k)+'</small><div>'+esc(v||'—')+'</div></p>').join('')+'</div><div class="card"><h2>Change portal password</h2><form id="change-password"><label>Current password</label><input name="currentPassword" type="password" autocomplete="current-password" required><label>New password (14+ characters)</label><input name="newPassword" type="password" autocomplete="new-password" minlength="14" required><p id="password-message" role="status"></p><button class="primary">Save password</button></form></div></div>';
  content.querySelector('#change-password').onsubmit=async e=>{e.preventDefault();
   const values=Object.fromEntries(new FormData(e.target));
   try{const response=await request('/password','POST',values);csrf=response.csrf;e.target.reset();content.querySelector('#password-message').textContent='Password changed. Other logged-in devices have been revoked.'}
   catch(err){content.querySelector('#password-message').textContent=err.message}
  };
 }
}
function dialog(html){const d=document.createElement('dialog');d.innerHTML=html;document.body.append(d);d.onclose=()=>d.remove();d.showModal();return d}
async function openInvoice(id){
 const invoice=state.invoices.find(i=>i.id===id);
 if(!invoice)return;
 try{
  const items=await request('/invoices/'+id+'/items');
  const d=dialog('<div class="toolbar"><h2>Invoice '+esc(invoice.number)+'</h2><button id="close">Close</button></div><p class="muted">Status: '+esc(invoice.status)+' · Total: '+esc(zar(invoice.total))+' · Paid: '+esc(zar(invoice.paid))+' · Credits: '+esc(zar(invoice.credited))+' · Outstanding: '+esc(zar(invoice.outstanding))+'</p><h3>Subscription line items</h3>'+(items.length?'<div class="tablewrap"><table><thead><tr><th>Service</th><th>Quantity</th><th>Unit price</th><th>Amount</th></tr></thead><tbody>'+items.map(x=>'<tr><td>'+esc(x.description)+'</td><td>'+esc(x.quantity)+'</td><td>'+esc(zar(x.unit_price))+'</td><td>'+esc(zar(x.amount))+'</td></tr>').join('')+'</tbody></table></div>':'<p class="note">Detailed itemization is unavailable for manually entered or legacy invoices.</p>'));
  d.querySelector('#close').onclick=()=>d.close();
 }catch(e){alert(e.message)}
}
function newTicket(){
 const d=dialog('<div class="toolbar"><h2>Open support ticket</h2><button type="button" id="cancel">Close</button></div><form id="form"><label>Subject</label><input name="subject" minlength="4" maxlength="200" required><label>Describe the problem</label><textarea class="tall" name="description" minlength="5" maxlength="3000" required></textarea><p class="error" id="message"></p><button class="primary">Submit ticket</button></form>');
 d.querySelector('#cancel').onclick=()=>d.close();
 d.querySelector('#form').onsubmit=async e=>{e.preventDefault();
  try{await request('/tickets','POST',Object.fromEntries(new FormData(e.target)));d.close();state.view='Tickets';await load()}
  catch(err){d.querySelector('#message').textContent=err.message}
 };
}
async function openTicket(id){
 const ticket=state.tickets.find(t=>t.id===id);if(!ticket)return;
 let messages;
 try{messages=await request('/tickets/'+id+'/messages')}
 catch(e){alert(e.message);return}
 const closed=['closed','resolved'].includes(ticket.status);
 const d=dialog('<div class="toolbar"><div><h2>'+esc(ticket.subject)+'</h2><p class="muted">Ticket #'+esc(ticket.number)+' · '+esc(ticket.status)+'</p></div><button id="close">Close</button></div><div class="message"><small>Original request</small><div>'+esc(ticket.description)+'</div></div><h3>Public conversation</h3>'+messages.map(m=>'<div class="message '+(m.actor_type==='staff'?'staff':'')+'"><small>'+esc(m.author)+' · '+esc(m.created_at)+'</small>'+esc(m.body)+'</div>').join('')+(closed?'<p class="note">This ticket has been closed. Create a new request if further assistance is required.</p>':'<form id="reply"><label>Your reply</label><textarea name="body" class="tall" maxlength="4000" required></textarea><p id="reply-error" class="error"></p><button class="primary">Send reply</button></form>'));
 d.querySelector('#close').onclick=()=>d.close();
 if(!closed)d.querySelector('#reply').onsubmit=async e=>{e.preventDefault();
  try{await request('/tickets/'+id+'/messages','POST',Object.fromEntries(new FormData(e.target)));d.close();await load();await openTicket(id)}
  catch(err){d.querySelector('#reply-error').textContent=err.message}
 };
}
logout.onclick=async()=>{try{await request('/logout','POST');login()}catch(e){alert(e.message)}};
boot();
