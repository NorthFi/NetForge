// root/selfhost/web/procurement.js
'use strict';
async function procurementConsole(el){
 const admin=role==='admin';
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>Procurement & highsite stock</h1><p class="sub">Purchase approvals · partial receipts · equipment deployment</p></div><button id="po-refresh">Refresh</button></div><p class="note">Unit costs are agreed commercial amounts in ZAR, excluding separately handled taxes or freight. No accounting posting is made. Receive and deploy only confirmed physical stock.</p><div id="po-actions"></div><div id="po-list"></div><div id="po-detail"></div>');
 const actions=el.querySelector('#po-actions'),list=el.querySelector('#po-list'),detail=el.querySelector('#po-detail');
 let suppliers=[],items=[],warehouses=[],view='orders',offset=0,selected=null,seq=0;
 const fail=e=>{if(list.isConnected)notice(e.message,true)};
 const opt=(v,l)=>'<option value="'+esc(v)+'">'+esc(l)+'</option>';
 const itemOptions=()=>items.map(i=>opt(i.id,i.sku+' · '+i.name+(i.serialized?' [serial]':''))).join('');
 const warehouseOptions=(branchOnly=false)=>warehouses.filter(w=>!branchOnly||!w.technician_id).map(w=>opt(w.id,w.branch+' · '+w.name)).join('');
 const vals=(...x)=>x.map(v=>esc(v??'—'));
 const table=(h,rows)=>'<div class="card tablewrap"><table><thead><tr>'+h.map(x=>'<th>'+esc(x)+'</th>').join('')+'</tr></thead><tbody>'+rows.map(r=>'<tr>'+r.map(x=>'<td>'+x+'</td>').join('')+'</tr>').join('')+'</tbody></table></div>';
 const serials=f=>f.get('serials').split(/\r?\n/).map(s=>s.trim()).filter(Boolean);
 const quantity='<label>Quantity</label><input name="quantity" type="number" min="1" max="100000" required><label>Serials (one per line; serialized stock only)</label><textarea name="serials" rows="4"></textarea>';
 function form(title,fields,save){
  const requestId=crypto.randomUUID(),d=dialog('<h2>'+esc(title)+'</h2><form>'+fields+'<p class="error"></p><button class="primary">Confirm</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  d.querySelector('form').onsubmit=async e=>{e.preventDefault();const b=e.target.querySelector('.primary');b.disabled=true;try{await save(new FormData(e.target),requestId);d.close();await refresh()}catch(err){d.querySelector('.error').textContent=err.message;b.disabled=false}};
 }
 function createOrder(){
  const d=dialog('<h2>Create purchase order</h2><form><label>Supplier</label><select name="supplierId" required>'+suppliers.filter(s=>s.enabled).map(s=>opt(s.id,s.name)).join('')+'</select><label>Receiving branch warehouse</label><select name="warehouseId" required>'+warehouseOptions(true)+'</select><label>Reference / scope</label><input name="reference" required minlength="3" maxlength="240"><label>Expected delivery (optional)</label><input name="expectedDay" type="date"><div id="po-lines"></div><button type="button" id="po-add">+ Line</button><p class="error"></p><button class="primary">Submit for approval</button><button type="button" id="cancel">Cancel</button></form>');
  const lines=d.querySelector('#po-lines'),f=d.querySelector('form');
  const add=()=>{if(lines.children.length>=50)return;const row=document.createElement('div');row.className='card';row.innerHTML='<label>Item</label><select name="itemId" required>'+itemOptions()+'</select><label>Ordered quantity</label><input name="quantity" type="number" min="1" max="100000" required><label>Agreed unit cost (ZAR)</label><input name="unitCost" type="number" min="0" max="9999999999.99" step="0.01" required><button type="button">Remove line</button>';row.querySelector('button').onclick=()=>{if(lines.children.length>1)row.remove()};lines.append(row)};
  add();d.querySelector('#po-add').onclick=add;d.querySelector('#cancel').onclick=()=>d.close();
  f.onsubmit=async e=>{e.preventDefault();const b=f.querySelector('.primary');b.disabled=true;const data=new FormData(f);
   try{await api('/procurement/orders','POST',{supplierId:data.get('supplierId'),warehouseId:data.get('warehouseId'),reference:data.get('reference'),expectedDay:data.get('expectedDay')||null,lines:[...lines.children].map(r=>({itemId:r.querySelector('[name=itemId]').value,quantity:Number(r.querySelector('[name=quantity]').value),unitCost:Number(r.querySelector('[name=unitCost]').value)})),confirm:true});d.close();await refresh()}catch(err){f.querySelector('.error').textContent=err.message;b.disabled=false}};
 }
 async function show(o,receiptOffset=0){
  const n=++seq,path='/procurement/orders/'+o.id;
  const [lines,receipts]=await Promise.all([api(path+'/lines'),api(path+'/receipts?offset='+receiptOffset)]);
  if(!detail.isConnected||n!==seq)return;
  detail.innerHTML='<div class="toolbar topgap"><div><h2>PO-'+esc(o.number)+' · '+esc(o.supplier_name)+'</h2><p>'+esc(o.reference)+'</p><p class="sub">'+esc(o.state)+' · '+esc(o.warehouse)+' · '+esc(o.creator)+' · Approved by '+esc(o.approver||'—')+' · Expected '+esc(o.expected_day||'—')+'</p></div><div class="buttons">'+(admin&&o.state==='pending'?'<button id="po-approve">Approve</button>':'')+(admin&&['pending','approved','partial'].includes(o.state)?'<button id="po-cancel">Cancel remainder</button>':'')+'</div></div>'+table(['SKU','Ordered','Received','Outstanding','Unit cost','Ordered value',''],lines.map(l=>[...vals(l.sku,l.quantity,l.received,l.outstanding,money(l.unit_cost),money(l.ordered_value)),['approved','partial'].includes(o.state)&&l.outstanding>0?'<button data-receive="'+esc(l.id)+'">Receive</button>':'']))+'<div class="toolbar topgap"><h3>Receipt history</h3><button id="po-rprev">Previous</button><button id="po-rnext">Next</button></div>'+table(['Date','Delivery reference','SKU','Qty','Serials','Cost','Actor'],receipts.map(r=>vals(r.received_at,r.delivery_reference,r.sku,r.quantity,(r.serials||[]).join(', '),money(r.received_value),r.username)));
  const prev=detail.querySelector('#po-rprev'),next=detail.querySelector('#po-rnext');prev.disabled=receiptOffset===0;next.disabled=receipts.length<100;prev.onclick=()=>show(o,Math.max(0,receiptOffset-100)).catch(fail);next.onclick=()=>show(o,receiptOffset+100).catch(fail);
  const approve=detail.querySelector('#po-approve');if(approve)approve.onclick=()=>{if(confirm('Independently approve frozen purchase quantities and costs?'))api(path+'/approve','POST',{confirm:true}).then(refresh).catch(fail)};
  const cancel=detail.querySelector('#po-cancel');if(cancel)cancel.onclick=()=>{if(confirm('Cancel outstanding quantities? Previously received stock is not reversed.'))api(path+'/cancel','POST',{confirm:true}).then(refresh).catch(fail)};
  detail.querySelectorAll('[data-receive]').forEach(b=>b.onclick=()=>{const l=lines.find(x=>x.id===b.dataset.receive);form('Receive '+l.sku+' — '+l.outstanding+' outstanding',quantity+'<label>Supplier delivery reference</label><input name="deliveryReference" required minlength="3" maxlength="180">',(f,id)=>api(path+'/receive','POST',{requestId:id,lineId:l.id,quantity:Number(f.get('quantity')),serials:serials(f),deliveryReference:f.get('deliveryReference'),confirm:true}))});
 }
 async function load(){
  const n=++seq;detail.innerHTML='';
  const paged=view!=='suppliers',rows=paged?await api('/procurement/'+view+'?offset='+offset):suppliers;
  if(!list.isConnected||n!==seq)return;
  actions.querySelector('#po-prev').disabled=!paged||offset===0;actions.querySelector('#po-next').disabled=!paged||rows.length<100;
  if(view==='orders'){
   list.innerHTML=table(['PO','Supplier','Warehouse','State','Ordered value','Received value',''],rows.map(o=>[...vals('PO-'+o.number,o.supplier_name,o.warehouse,o.state,money(o.ordered_value),money(o.received_value)),'<button data-order="'+esc(o.id)+'">Inspect</button>']));
   list.querySelectorAll('[data-order]').forEach(b=>b.onclick=()=>{selected=b.dataset.order;show(rows.find(o=>o.id===selected)).catch(fail)});
   if(selected){const o=rows.find(x=>x.id===selected);if(o)await show(o);else selected=null}
  }else if(view==='suppliers'){
   list.innerHTML=table(['Name','Account','Email','Phone','Status',''],rows.map(s=>[...vals(s.name,s.account_reference,s.email,s.phone,s.enabled?'Enabled':'Disabled'),admin?'<button data-supplier="'+esc(s.id)+'">'+(s.enabled?'Disable':'Enable')+'</button>':'']));
   list.querySelectorAll('[data-supplier]').forEach(b=>b.onclick=()=>{const s=rows.find(x=>x.id===b.dataset.supplier);if(confirm('Change supplier status? Existing approved orders remain receivable.'))api('/procurement/suppliers/'+s.id+'/enabled','PUT',{enabled:!s.enabled,confirm:true}).then(refresh).catch(fail)});
  }else if(view==='reorder'){
   list.innerHTML='<p class="note">Suggestions only: minimum minus available stock minus outstanding approved orders. No automatic purchase order.</p>'+table(['Warehouse','SKU','Available','Minimum','Incoming','Suggested'],rows.map(r=>vals(r.warehouse,r.sku,r.available,r.min_available,r.incoming,r.suggested)));
  }else{
   list.innerHTML=table(['Date','Highsite','Warehouse','SKU','Qty','Serials','Reference','Actor'],rows.map(r=>vals(r.deployed_at,r.highsite,r.warehouse,r.sku,r.quantity,(r.serials||[]).join(', '),r.reference,r.username)));
  }
 }
 async function refresh(){
  [suppliers,items,warehouses]=await Promise.all([api('/procurement/suppliers'),api('/stock/items'),api('/stock/warehouses')]);if(!list.isConnected)return;
  actions.innerHTML='<div class="buttons">'+(admin?'<button id="po-supplier">+ Supplier</button>':'')+'<button class="primary" id="po-new">+ Purchase order</button><button id="po-deploy">Deploy highsite stock</button></div><div class="buttons"><select id="po-view"><option value="orders">Orders</option><option value="suppliers">Suppliers</option><option value="reorder">Reorder suggestions</option><option value="highsite-deployments">Highsite deployments</option></select><button id="po-prev">Previous</button><button id="po-next">Next</button></div>';
  actions.querySelector('#po-view').value=view;actions.querySelector('#po-view').onchange=e=>{view=e.target.value;offset=0;selected=null;load().catch(fail)};
  actions.querySelector('#po-prev').onclick=()=>{offset=Math.max(0,offset-100);load().catch(fail)};actions.querySelector('#po-next').onclick=()=>{offset+=100;load().catch(fail)};
  if(admin)actions.querySelector('#po-supplier').onclick=()=>form('Create supplier','<label>Name</label><input name="name" required maxlength="160"><label>Account reference</label><input name="accountReference" maxlength="100"><label>Email</label><input name="email" type="email" maxlength="254"><label>Phone</label><input name="phone" maxlength="70">',f=>api('/procurement/suppliers','POST',Object.fromEntries(f)));
  actions.querySelector('#po-new').onclick=createOrder;
  actions.querySelector('#po-deploy').onclick=async()=>{try{const sites=await api('/highsites');form('Confirm physical highsite deployment','<label>Highsite</label><select name="highsiteId" required>'+sites.map(s=>opt(s.id,s.number+' · '+s.name)).join('')+'</select><label>Source warehouse</label><select name="warehouseId" required>'+warehouseOptions()+'</select><label>Item</label><select name="itemId" required>'+itemOptions()+'</select>'+quantity+'<label>Work reference</label><input name="reference" minlength="3" maxlength="240" required><p>Deduct unreserved stock and create linked highsite assets for serialized units. No automatic reversal.</p>',(f,id)=>api('/procurement/highsite-deployments','POST',{requestId:id,highsiteId:f.get('highsiteId'),warehouseId:f.get('warehouseId'),itemId:f.get('itemId'),quantity:Number(f.get('quantity')),serials:serials(f),reference:f.get('reference'),confirm:true}))}catch(e){fail(e)}};
  await load();
 }
 el.querySelector('#po-refresh').onclick=()=>refresh().catch(fail);
 try{await refresh()}catch(e){fail(e)}
}
