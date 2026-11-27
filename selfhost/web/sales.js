// root/selfhost/web/sales.js
'use strict';
async function salesConsole(el){
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>Sales & quotations</h1><p class="sub">Lead qualification · issued quotes · recorded acceptance</p></div><div class="buttons"><button id="sales-refresh">Refresh</button><button class="primary" id="sales-new">+ Lead</button></div></div><div class="buttons"><select id="sales-filter"><option value="">All states</option><option>new</option><option>qualified</option><option>lost</option><option>converted</option></select><button id="sales-prev">Previous</button><button id="sales-next">Next</button></div><div id="sales-list"></div><div id="sales-detail"></div>');
 const list=el.querySelector('#sales-list'),detail=el.querySelector('#sales-detail');
 let offset=0,selected=null,seq=0;
 const fail=e=>{if(list.isConnected)notice(e.message,true)};
 const option=(id,name)=>'<option value="'+esc(id)+'">'+esc(name)+'</option>';
 function form(title,fields,save){
  const d=dialog('<h2>'+esc(title)+'</h2><form>'+fields+'<p class="error"></p><button class="primary">Confirm</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  d.querySelector('form').onsubmit=async e=>{e.preventDefault();const b=e.target.querySelector('.primary');b.disabled=true;try{await save(new FormData(e.target));d.close();await load()}catch(err){d.querySelector('.error').textContent=err.message;b.disabled=false}};
 }
 async function show(){
  const l=selected,n=++seq,path='/sales/leads/'+l.id;
  const quotes=await api(path+'/quotes');if(!list.isConnected||n!==seq)return;
  detail.innerHTML='<div class="toolbar topgap"><div><h2>Lead '+esc(l.number)+' · '+esc(l.first_name)+' '+esc(l.surname)+'</h2><p>'+esc(l.address)+' · '+esc(l.phone)+' · '+esc(l.email)+'</p><p class="sub">'+esc(l.branch)+' · '+esc(l.state)+' · '+esc(l.source)+'</p><p>'+esc(l.notes)+'</p></div><div class="buttons">'+(l.state==='new'?'<button id="sales-qualify">Qualify</button>':'')+(['new','qualified'].includes(l.state)?'<button id="sales-lost">Close lost</button>':'')+(l.state==='qualified'?'<button id="sales-quote" class="primary">Issue quote</button>':'')+'</div></div><div class="card tablewrap"><table><thead><tr><th>Quote</th><th>Package</th><th>Monthly (ZAR)</th><th>Installation (ZAR)</th><th>State / expiry</th><th></th></tr></thead><tbody>'+quotes.map(q=>'<tr><td>Q-'+esc(q.number)+'</td><td>'+esc(q.package_name)+' · '+esc(q.download_mbps)+'/'+esc(q.upload_mbps)+' Mbps</td><td>'+esc(money(q.monthly_price))+'</td><td>'+esc(money(q.installation_price))+'</td><td>'+esc(q.state)+' · '+esc(q.valid_until)+'</td><td><button data-quote="'+esc(q.id)+'">Details</button>'+(q.state==='issued'?'<button data-withdraw="'+esc(q.id)+'">Withdraw</button><button data-accept="'+esc(q.id)+'">Record acceptance</button>':'')+'</td></tr>').join('')+'</tbody></table></div>';
  const qualify=detail.querySelector('#sales-qualify');if(qualify)qualify.onclick=()=>{if(confirm('Confirm qualification?'))api(path+'/state','POST',{state:'qualified',confirm:true}).then(load).catch(fail)};
  const lost=detail.querySelector('#sales-lost');if(lost)lost.onclick=()=>{if(confirm('Close lead as lost and withdraw outstanding quote?'))api(path+'/state','POST',{state:'lost',confirm:true}).then(load).catch(fail)};
  const issue=detail.querySelector('#sales-quote');if(issue)issue.onclick=async()=>{try{const packages=await api('/packages');form('Issue immutable quotation','<label>Package (current monthly price)</label><select name="packageId" required>'+packages.map(p=>option(p.id,p.name+' · '+money(p.monthly_price))).join('')+'</select><label>Installation charge (ZAR)</label><input name="installationPrice" type="number" min="0" step="0.01" value="0" required><label>Valid days (1–90)</label><input name="validDays" type="number" min="1" max="90" value="14" required><label>Terms, tax treatment and scope</label><textarea name="terms" required minlength="5" maxlength="2000" rows="5"></textarea><p>Prices and terms are frozen at issue. Send the quotation separately; no message is sent automatically.</p>',f=>api(path+'/quotes','POST',{packageId:f.get('packageId'),installationPrice:Number(f.get('installationPrice')),validDays:Number(f.get('validDays')),terms:f.get('terms'),confirm:true}))}catch(e){fail(e)}};
  detail.querySelectorAll('[data-quote]').forEach(b=>b.onclick=()=>{const q=quotes.find(x=>x.id===b.dataset.quote);const d=dialog('<h2>Quotation Q-'+esc(q.number)+'</h2><p>'+esc(q.contact_name)+' · '+esc(q.service_address)+'</p><p>'+esc(q.package_name)+' · '+esc(money(q.monthly_price))+' monthly · '+esc(money(q.installation_price))+' installation</p><p>Valid until '+esc(q.valid_until)+'</p><label>Frozen terms</label><textarea readonly rows="8">'+esc(q.terms)+'</textarea>'+(q.acceptance_id?'<p>Acceptance: '+esc(q.accepted_by_name)+' · '+esc(q.evidence_reference)+' · '+esc(q.recorded_at)+' · Account '+esc(q.customer_account)+'</p>':'')+'<button id="close">Close</button>');d.querySelector('#close').onclick=()=>d.close()});
  detail.querySelectorAll('[data-withdraw]').forEach(b=>b.onclick=()=>{if(confirm('Withdraw this quotation permanently?'))api(path+'/quotes/'+b.dataset.withdraw+'/withdraw','POST',{confirm:true}).then(load).catch(fail)});
  detail.querySelectorAll('[data-accept]').forEach(b=>b.onclick=()=>form('Record customer acceptance','<p>Confirm independently retained customer acceptance. Creates a pending customer and installation order; does not activate billing or RADIUS.</p><label>New unique account number</label><input name="account" required maxlength="100"><label>Accepting party</label><input name="acceptedByName" required maxlength="160"><label>Evidence reference (document/email/ticket)</label><input name="evidenceReference" required minlength="5" maxlength="240">',f=>api(path+'/quotes/'+b.dataset.accept+'/accept','POST',{...Object.fromEntries(f),confirm:true})));
 }
 async function load(){
  const filter=el.querySelector('#sales-filter').value;const params=new URLSearchParams({offset:String(offset)});if(filter)params.set('state',filter);
  const rows=await api('/sales/leads?'+params);if(!list.isConnected)return;
  el.querySelector('#sales-prev').disabled=offset===0;el.querySelector('#sales-next').disabled=rows.length<100;
  list.innerHTML='<div class="card tablewrap"><table><thead><tr><th>Lead</th><th>Contact</th><th>Branch</th><th>State</th><th>Account</th><th></th></tr></thead><tbody>'+rows.map(l=>'<tr><td>'+esc(l.number)+'</td><td>'+esc(l.first_name)+' '+esc(l.surname)+'</td><td>'+esc(l.branch)+'</td><td>'+esc(l.state)+'</td><td>'+esc(l.customer_account||'—')+'</td><td><button data-lead="'+esc(l.id)+'">Inspect</button></td></tr>').join('')+'</tbody></table></div>';
  list.querySelectorAll('[data-lead]').forEach(b=>b.onclick=()=>{selected=rows.find(l=>l.id===b.dataset.lead);show().catch(fail)});
  if(selected){selected=rows.find(l=>l.id===selected.id)||null;if(selected)await show();else detail.innerHTML=''}
 }
 el.querySelector('#sales-new').onclick=async()=>{try{const branches=await api('/branches');form('New lead','<label>First name</label><input name="firstName" required maxlength="120"><label>Surname</label><input name="surname" maxlength="120"><label>Email</label><input name="email" type="email" maxlength="254"><label>Phone</label><input name="phone" maxlength="70"><label>Service address</label><textarea name="address" required maxlength="500"></textarea><label>Branch</label><select name="branchId" required>'+branches.map(b=>option(b.id,b.name)).join('')+'</select><label>Source</label><input name="source" maxlength="80"><label>Notes</label><textarea name="notes" maxlength="2000"></textarea>',f=>api('/sales/leads','POST',Object.fromEntries(f)))}catch(e){fail(e)}};
 el.querySelector('#sales-filter').onchange=()=>{offset=0;load().catch(fail)};
 el.querySelector('#sales-prev').onclick=()=>{offset=Math.max(0,offset-100);load().catch(fail)};
 el.querySelector('#sales-next').onclick=()=>{offset+=100;load().catch(fail)};
 el.querySelector('#sales-refresh').onclick=()=>load().catch(fail);
 try{await load()}catch(e){fail(e)}
}
async function installationsConsole(el){
 const staff=['admin','dispatcher'].includes(role),admin=role==='admin';
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>Installation planner</h1><p class="sub">Branch-based day planning · no time slots</p></div><div class="buttons"><button id="ins-refresh">Refresh</button>'+(admin?'<button id="ins-roster">Branch roster</button>':'')+'</div></div><div class="buttons"><select id="ins-branch"><option value="">All branches</option></select><input id="ins-day" type="date" aria-label="Scheduled day"><button id="ins-all">All orders</button><button id="ins-unscheduled">Unscheduled</button><button id="ins-prev">Previous page</button><button id="ins-next">Next page</button></div><div id="ins-list"></div><div id="ins-detail"></div>');
 const list=el.querySelector('#ins-list'),detail=el.querySelector('#ins-detail'),branch=el.querySelector('#ins-branch'),day=el.querySelector('#ins-day');
 let offset=0,unscheduled=false,selected=null,seq=0;
 const fail=e=>{if(list.isConnected)notice(e.message,true)};
 const opt=(id,name)=>'<option value="'+esc(id)+'">'+esc(name)+'</option>';
 function form(title,fields,save){
  const d=dialog('<h2>'+esc(title)+'</h2><form>'+fields+'<p class="error"></p><button class="primary">Confirm</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  d.querySelector('form').onsubmit=async e=>{e.preventDefault();const b=e.target.querySelector('.primary');b.disabled=true;try{await save(new FormData(e.target));d.close();await load()}catch(err){d.querySelector('.error').textContent=err.message;b.disabled=false}};
 }
 async function show(){
  const o=selected,n=++seq,path='/installations/orders/'+o.id,events=await api(path+'/events');
  if(n!==seq||!list.isConnected)return;
  const actions=[];
  if(o.state==='scheduled')actions.push(['in_progress','Start'],['blocked','Block']);
  if(o.state==='in_progress')actions.push(['completed','Complete'],['blocked','Block']);
  if(staff&&['unscheduled','scheduled','blocked'].includes(o.state))actions.push(['cancelled','Cancel']);
  detail.innerHTML='<div class="toolbar topgap"><div><h2>Installation '+esc(o.number)+' · '+esc(o.account)+'</h2><p>'+esc(o.first_name)+' '+esc(o.surname)+' · '+esc(o.phone)+' · '+esc(o.address)+'</p><p class="sub">Q-'+esc(o.quote_number)+' · Ticket '+esc(o.ticket_number)+' · '+esc(o.package_name)+' · '+esc(o.branch)+'</p><p>'+esc(o.result_note||'')+'</p></div><div class="buttons">'+(staff&&['unscheduled','scheduled','blocked'].includes(o.state)?'<button id="ins-schedule">Assign / reschedule</button>':'')+actions.map(a=>'<button data-state="'+esc(a[0])+'">'+esc(a[1])+'</button>').join('')+'</div></div><div class="card tablewrap"><table><thead><tr><th>Time</th><th>Staff</th><th>Action</th><th>Note</th></tr></thead><tbody>'+events.map(e=>'<tr><td>'+esc(e.occurred_at)+'</td><td>'+esc(e.username)+'</td><td>'+esc(e.action)+'</td><td>'+esc(e.note)+'</td></tr>').join('')+'</tbody></table></div>';
  const schedule=detail.querySelector('#ins-schedule');if(schedule)schedule.onclick=async()=>{try{const roster=(await api('/installations/roster?branch='+o.branch_id)).filter(r=>r.assigned&&r.enabled);if(!roster.length)throw Error('Assign an enabled technician to this branch first');form('Schedule installation','<label>Technician</label><select name="technicianId" required>'+roster.map(r=>'<option value="'+esc(r.id)+'" '+(r.id===o.technician_id?'selected':'')+'>'+esc(r.username)+'</option>').join('')+'</select><label>Scheduled day</label><input name="day" type="date" required value="'+esc(o.scheduled_day||day.value)+'"><label>Planning note</label><textarea name="note" maxlength="1000"></textarea>',f=>api(path+'/schedule','POST',{...Object.fromEntries(f),confirm:true}))}catch(e){fail(e)}};
  detail.querySelectorAll('[data-state]').forEach(b=>b.onclick=()=>form('Record '+b.dataset.state,'<label>Result / verification note</label><textarea name="note" minlength="5" maxlength="1000" required></textarea><p>Work order status does not activate service, create invoices or close the linked ticket.</p>',f=>api(path+'/state','POST',{state:b.dataset.state,note:f.get('note'),confirm:true})));
 }
 async function load(){
  const p=new URLSearchParams({offset:String(offset)});if(branch.value)p.set('branch',branch.value);if(day.value)p.set('day',day.value);if(unscheduled)p.set('unscheduled','true');
  const orders=await api('/installations/orders?'+p);if(!list.isConnected)return;
  el.querySelector('#ins-prev').disabled=offset===0;el.querySelector('#ins-next').disabled=orders.length<100;
  list.innerHTML='<div class="card tablewrap"><table><thead><tr><th>Day</th><th>Technician</th><th>Branch</th><th>Order / account</th><th>State</th><th></th></tr></thead><tbody>'+orders.map(o=>'<tr><td>'+esc(o.scheduled_day||'Unscheduled')+'</td><td>'+esc(o.technician||'—')+'</td><td>'+esc(o.branch)+'</td><td>'+esc(o.number)+' / '+esc(o.account)+'</td><td>'+esc(o.state)+'</td><td><button data-order="'+esc(o.id)+'">Inspect</button></td></tr>').join('')+'</tbody></table></div>';
  list.querySelectorAll('[data-order]').forEach(b=>b.onclick=()=>{selected=orders.find(o=>o.id===b.dataset.order);show().catch(fail)});
  if(selected){selected=orders.find(o=>o.id===selected.id)||null;if(selected)await show();else detail.innerHTML=''}
 }
 const reload=()=>{offset=0;load().catch(fail)};
 branch.onchange=reload;day.onchange=()=>{unscheduled=false;reload()};
 el.querySelector('#ins-all').onclick=()=>{day.value='';unscheduled=false;reload()};
 el.querySelector('#ins-unscheduled').onclick=()=>{day.value='';unscheduled=true;reload()};
 el.querySelector('#ins-prev').onclick=()=>{offset=Math.max(0,offset-100);load().catch(fail)};
 el.querySelector('#ins-next').onclick=()=>{offset+=100;load().catch(fail)};
 el.querySelector('#ins-refresh').onclick=()=>load().catch(fail);
 if(admin)el.querySelector('#ins-roster').onclick=async()=>{try{if(!branch.value)throw Error('Select a branch first');const id=branch.value,roster=await api('/installations/roster?branch='+id);form('Branch technician roster','<label>Technician</label><select name="userId" required>'+roster.map(r=>opt(r.id,r.username+' · '+(r.assigned?'assigned':'not assigned')+(r.enabled?'':' · disabled'))).join('')+'</select><label>Action</label><select name="enabled"><option value="true">Assign to branch</option><option value="false">Remove from branch</option></select>',f=>api('/installations/roster/'+id,'PUT',{userId:f.get('userId'),enabled:f.get('enabled')==='true',confirm:true}))}catch(e){fail(e)}};
 try{const branches=await api('/branches');if(!list.isConnected)return;branch.insertAdjacentHTML('beforeend',branches.map(b=>opt(b.id,b.name)).join(''));await load()}catch(e){fail(e)}
}
