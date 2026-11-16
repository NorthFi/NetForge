// root/portal/assets/portal.js
'use strict';
const app=document.querySelector('#app'),logoutButton=document.querySelector('#logout'),identity=document.querySelector('#identity');
let csrf='',snapshot=null,view='Overview';
const safe=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[c]));
const money=x=>new Intl.NumberFormat('en-ZA',{style:'currency',currency:'ZAR'}).format(Number(x)||0);
async function request(action,method='GET',body){
 const opts={method,credentials:'same-origin',headers:{'Accept':'application/json'}};
 if(method!=='GET')opts.headers['X-CSRF-Token']=csrf;
 if(body!==undefined){opts.headers['Content-Type']='application/json';opts.body=JSON.stringify(body)}
 const response=await fetch('api.php?action='+encodeURIComponent(action),opts);
 let payload;try{payload=await response.json()}catch{throw Error('Customer portal returned an invalid response')}
 if(!response.ok)throw Error(payload.error||'Request failed');
 return payload;
}
function login(){
 logoutButton.hidden=true;identity.textContent='';
 app.innerHTML='<main><section class="card login"><h1>Welcome back</h1><p class="muted">Sign in with the customer portal credentials issued by your ISP.</p><form id="loginform"><label for="username">Portal username</label><input id="username" name="username" required autocomplete="username" maxlength="64"><label for="password">Password</label><input id="password" name="password" type="password" required autocomplete="current-password"><p id="error" role="alert"></p><button class="primary" type="submit">Sign in</button></form></section></main>';
 app.querySelector('form').onsubmit=async event=>{
  event.preventDefault();const values=Object.fromEntries(new FormData(event.target));
  try{const x=await request('login','POST',values);csrf=x.csrf;await reload()}
  catch(e){app.querySelector('#error').textContent=e.message}
 };
}
async function boot(){
 try{const status=await request('status');csrf=status.csrf||'';if(!status.authenticated)return login();await reload()}
 catch(e){app.innerHTML='<main class="card"><p class="error">'+safe(e.message)+'</p></main>'}
}
async function reload(){
 try{snapshot=await request('data');logoutButton.hidden=false;identity.textContent=snapshot.customer.account||'';render()}
 catch(e){if(/login|revoked/i.test(e.message))login();else app.innerHTML='<main class="card"><p class="error">'+safe(e.message)+'</p></main>'}
}
function table(items,columns){
 return '<div class="card tablewrap"><table><thead><tr>'+columns.map(([key,label])=>'<th>'+safe(label)+'</th>').join('')+'</tr></thead><tbody>'+items.map(item=>'<tr'+(item.id&&columns.some(([k])=>k==='name')?' data-ticket="'+safe(item.id)+'"':'')+'>'+columns.map(([key])=>'<td>'+safe(item[key]||'—')+'</td>').join('')+'</tr>').join('')+'</tbody></table>'+(items.length?'':'<p class="muted">No records yet.</p>')+'</div>';
}
function render(){
 if(!snapshot)return login();
 const c=snapshot.customer;
 const tabs=['Overview','Services','Usage','Invoices','Tickets','Equipment','Profile'];
 app.innerHTML='<main><h1>Hello, '+safe(c.name||c.account)+'</h1><p class="muted">Account '+safe(c.account)+' · '+safe(c.account_status||'Status not configured')+'</p><nav class="tabs" id="tabs" aria-label="Portal sections">'+tabs.map(t=>'<button data-view="'+safe(t)+'" class="'+(view===t?'active':'')+'">'+safe(t)+'</button>').join('')+'</nav><section id="content"></section></main>';
 app.querySelectorAll('[data-view]').forEach(b=>b.onclick=()=>{view=b.dataset.view;render()});
 const target=app.querySelector('#content'),services=snapshot.services,tickets=snapshot.tickets,invoices=snapshot.invoices,payments=snapshot.payments;
 if(view==='Overview'){
  const paid=payments.reduce((n,x)=>n+(Number(x.amount)||0),0);
  const due=invoices.reduce((n,x)=>n+(Number(x.amount)||0),0)-paid;
  target.innerHTML='<div class="grid">'+[['Active services',services.filter(x=>x.status==='active').length],['Open support tickets',tickets.filter(x=>!['resolved','closed'].includes(x.status)).length],['Visible invoices',invoices.length],['Recorded invoice/payment difference',money(due)]].map(([k,v])=>'<div class="card"><small>'+safe(k)+'</small><div class="metric">'+safe(v)+'</div></div>').join('')+'</div><div class="warn">Balances are informational, based on recorded invoices and payments. Payments cannot be submitted through this portal.</div><div class="card"><h2>Quick actions</h2><div class="toolbar"><button id="new-ticket" class="primary">Open support ticket</button><button id="view-invoices">View invoices</button><button id="view-services">My services</button></div></div>';
  target.querySelector('#new-ticket').onclick=newTicket;
  target.querySelector('#view-invoices').onclick=()=>{view='Invoices';render()};
  target.querySelector('#view-services').onclick=()=>{view='Services';render()};
 }else if(view==='Services'){
  target.innerHTML='<h2>My services</h2>'+table(services,[['name','Service'],['service_type','Type'],['package','Package'],['status','Status'],['activated_date','Activated']]);
 }else if(view==='Usage'){
  target.innerHTML='<h2>Monthly data usage</h2><p class="muted">These records are manually entered or imported summaries, not live router statistics.</p>'+table(snapshot.usage,[['period','Month'],['download_gb','Download GB'],['upload_gb','Upload GB'],['cap_gb','Data cap GB']]);
 }else if(view==='Invoices'){
  const visible=invoices.map(i=>({...i,amount:money(i.amount)}));
  const pay=payments.map(p=>({...p,amount:money(p.amount)}));
  target.innerHTML='<h2>Invoices and payments</h2>'+table(visible,[['number','Invoice'],['name','Description'],['invoice_date','Issued'],['due_date','Due'],['amount','Amount'],['status','Status']])+'<h2 style="margin-top:20px">Recorded payments</h2>'+table(pay,[['reference','Reference'],['payment_date','Date'],['amount','Amount'],['method','Method']])+'<p class="muted">This portal cannot accept cards, bank details or payment instructions.</p>';
 }else if(view==='Tickets'){
  target.innerHTML='<div class="toolbar"><h2>Support tickets</h2><button class="primary" id="new-ticket">+ Open ticket</button></div>'+table(tickets,[['number','Number'],['name','Subject'],['status','Status'],['priority','Priority'],['created_at','Created']]);
  target.querySelector('#new-ticket').onclick=newTicket;
  target.querySelectorAll('tr[data-ticket]').forEach(tr=>tr.onclick=()=>ticket(tr.dataset.ticket));
 }else if(view==='Equipment'){
  target.innerHTML='<h2>My equipment</h2><p class="muted">For safety, management addresses and device credentials are not displayed here.</p>'+table(snapshot.equipment,[['name','Device'],['type','Model'],['category','Category'],['serial_number','Serial'],['ownership','Ownership'],['warranty_expiry','Warranty']]);
 }else if(view==='Profile'){
  target.innerHTML='<div class="grid"><div class="card"><h2>Profile</h2>'+[['Account',c.account],['Name',(c.name||'')+' '+(c.surname||'')],['Phone',c.phone],['Email',c.email],['Address',c.address],['Package',c.package]].map(([key,value])=>'<p><small>'+safe(key)+'</small><br><b>'+safe(value||'—')+'</b></p>').join('')+'</div><div class="card"><h2>Change portal password</h2><form id="passwordform"><label>Current password</label><input name="current_password" type="password" required autocomplete="current-password"><label>New password</label><input name="new_password" type="password" minlength="14" required autocomplete="new-password"><p id="pw-result" role="status"></p><button class="primary">Update password</button></form></div></div>';
  target.querySelector('#passwordform').onsubmit=async e=>{e.preventDefault();const values=Object.fromEntries(new FormData(e.target));
   try{await request('password','POST',values);e.target.reset();target.querySelector('#pw-result').textContent='Password changed. Your other sessions will be revoked.'}catch(err){target.querySelector('#pw-result').textContent=err.message}};
 }
}
function modal(content){
 const d=document.createElement('dialog');d.innerHTML=content;document.body.append(d);d.onclose=()=>d.remove();d.showModal();return d;
}
function newTicket(){
 const d=modal('<h2>New support ticket</h2><p class="muted">Describe the issue so your support team has enough information to assist.</p><form id="ticketform"><label>Subject</label><input name="name" minlength="4" maxlength="160" required><label>Description</label><textarea name="description" rows="6" minlength="5" maxlength="3000" required></textarea><p class="error" id="formerror"></p><div class="toolbar"><button class="primary">Submit ticket</button><button type="button" id="close">Cancel</button></div></form>');
 d.querySelector('#close').onclick=()=>d.close();
 d.querySelector('#ticketform').onsubmit=async e=>{e.preventDefault();try{await request('ticket','POST',Object.fromEntries(new FormData(e.target)));d.close();view='Tickets';await reload()}catch(err){d.querySelector('#formerror').textContent=err.message}};
}
function ticket(id){
 const record=snapshot.tickets.find(x=>x.id===id);if(!record)return;
 const messages=snapshot.messages.filter(x=>x.ticket_id===id).sort((a,b)=>String(a.created_at).localeCompare(String(b.created_at)));
 const closed=['resolved','closed'].includes(record.status);
 const d=modal('<div class="toolbar"><div><h2>'+safe(record.name)+'</h2><small>'+safe(record.number)+' · '+safe(record.status)+'</small></div><button id="close">Close</button></div><div class="message"><small>Original ticket</small><p>'+safe(record.description||'No description')+'</p></div><h3>Conversation</h3><div id="messages">'+(messages.map(m=>'<div class="message '+(m.source==='portal'?'':'staff')+'"><small>'+safe(m.author)+' · '+safe(m.created_at)+'</small>'+safe(m.body)+'</div>').join('')||'<p class="muted">No replies yet.</p>')+'</div>'+(closed?'<p class="warn">This ticket has been closed. Open a new ticket if you need further assistance.</p>':'<form id="replyform"><label>Reply</label><textarea name="body" required rows="4" maxlength="4000" minlength="1"></textarea><p class="error" id="replyerror"></p><button class="primary">Send reply</button></form>'));
 d.querySelector('#close').onclick=()=>d.close();
 const form=d.querySelector('#replyform');
 if(form)form.onsubmit=async e=>{e.preventDefault();const body=new FormData(e.target).get('body');
  try{await request('reply','POST',{ticket_id:id,body});d.close();await reload();ticket(id)}catch(err){d.querySelector('#replyerror').textContent=err.message}};
}
logoutButton.onclick=async()=>{try{await request('logout','POST');csrf='';snapshot=null;view='Overview';login()}catch(err){alert(err.message)}};
boot();
